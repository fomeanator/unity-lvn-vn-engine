using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lvn.Content
{
    /// <summary>
    /// ПОЛОСА: сколько работ одного вида идёт разом — и кто заходит первым.
    ///
    /// <para>Правило «сколько сразу» стояло в трёх местах и в каждом по своей
    /// причине: двенадцать мест в сети (потоки HTTP/2), три места у распаковки
    /// (чтобы выгрузка в видеопамять не приезжала одним залпом), двенадцать/
    /// шесть/два у расписания главы (чтобы пара крупных файлов не заняла
    /// соединение). Три верных числа — и ни одно из них не знало ГЛАВНОГО:
    /// ждёт ли этот файл поверхность прямо сейчас.</para>
    ///
    /// <para>Из-за этого живая картинка стояла в очереди за фоновым прогревом
    /// НЕ ПО НЕВЕЗЕНИЮ, А ПО УСТРОЙСТВУ: мест ровно столько, кто первым попросил
    /// — того и место. Игрок при этом смотрел на пустую рамку.</para>
    ///
    /// <para><b>Полоса шириной W держит K мест для живого.</b> Фоновая работа
    /// физически не может занять больше <c>W-K</c>, поэтому живому всегда есть
    /// куда встать.</para>
    ///
    /// <para><b>Брони мало, когда живого много.</b> Бронь спасает ОДНО живое
    /// дело: она не даёт фону занять последние места. Но у актёра слоёв
    /// пять-восемь, и все они живые — третий такой запрос встаёт в очередь за
    /// фоном честно, по устройству полосы. Поэтому у полосы есть и второй
    /// приём: попросить фон УСТУПИТЬ место.</para>
    ///
    /// <para><b>Уступка — не отмена.</b> Тот, кого попросили, получает свой
    /// признак (<see cref="Pass.Yield"/>), обрывает работу и ЗАХОДИТ СНОВА,
    /// когда живое прошло. Для его вызывающего ничего не случилось: он не
    /// видит ни отмены, ни ошибки — только более долгую загрузку. Отмена
    /// самого вызывающего при этом остаётся отменой: это разные признаки, и
    /// путать их нельзя — иначе «игрок вышел из главы» станет «повторим
    /// позже».</para>
    ///
    /// <para><b>Что теряет уступивший — по-разному, и это надо знать.</b>
    /// Заход с докачкой продолжится с последнего дописанного в <c>.part</c>.
    /// А пакетный заход главы держит байты в памяти и на диск в полёте не
    /// пишет — он начнётся С НУЛЯ. Обещать «остаётся кусок» для обоих было бы
    /// неправдой: у половины путей куска нет.</para>
    ///
    /// <para>Цена ограничена одним файлом на уступку, и просят самого давнего
    /// — того, кто ближе к концу. Сделать её нулевой стоило бы записи каждого
    /// пакета на диск, и это дороже пропажи.</para>
    /// </summary>
    public sealed class LvnLane
    {
        private readonly SemaphoreSlim _all;        // все места полосы
        private readonly SemaphoreSlim _background; // места, доступные НЕ живому

        /// <param name="name">Как полоса зовётся в диагностике.</param>
        /// <param name="width">Сколько работ идёт разом.</param>
        /// <param name="keptForLive">Сколько мест из них фоновая работа занять
        /// не может. Ноль — полоса без брони: так ведут себя полосы, по которым
        /// живое не ходит вовсе.</param>
        public LvnLane(string name, int width, int keptForLive)
        {
            if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (keptForLive < 0 || keptForLive >= width)
                throw new ArgumentOutOfRangeException(nameof(keptForLive),
                    "бронь должна быть меньше ширины: полоса, целиком отданная живому, "
                    + "останавливает фон навсегда");
            Name = name;
            Width = width;
            KeptForLive = keptForLive;
            _all = new SemaphoreSlim(width, width);
            _background = new SemaphoreSlim(width - keptForLive, width - keptForLive);
        }

        public string Name { get; }
        public int Width { get; }
        public int KeptForLive { get; }

        /// <summary>Сколько мест свободно прямо сейчас. Нужно ДИАГНОСТИКЕ и
        /// проверкам: «место вернулось на любом выходе» — правило, которое не
        /// видно ниоткуда, кроме этого числа, а стоит его нарушение
        /// остановки всех загрузок до перезапуска приложения.</summary>
        public int Free => _all.CurrentCount;

        /// <summary>Занять место. Ступень берётся у того, кто просит, а если он
        /// молчит — у окружения (<see cref="LvnRungScope"/>).</summary>
        public Task<Pass> EnterAsync(CancellationToken ct) => EnterAsync(LvnRungScope.Current, ct);

        public async Task<Pass> EnterAsync(LvnRung rung, CancellationToken ct)
        {
            // ЗАМЕР — ЕДИНСТВЕННЫЙ СПОСОБ УВИДЕТЬ «ОБЪЯВЛЕНО НЕ ТОМУ».
            // Строку с неправильным адресатом не отличить от правильной ни
            // чтением, ни тестом дома; зато сорок шесть ЖИВЫХ входов за главу
            // при десяти актёрах на экране видно с первого взгляда.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            bool live = rung == LvnRung.Live;
            if (!live)
            {
                await _background.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _all.WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    _background.Release();
                    throw;
                }
                var seat = new Seat { Background = true };
                seat.Token = seat.Yield.Token;
                lock (_seats) _seats.Add(seat);
                LvnLaneWatch.Entered(Name, rung, waited.ElapsedMilliseconds);
                return new Pass(this, seat);
            }

            // ЖИВОЕ ПРОСИТ ФОН УСТУПИТЬ — до того, как встать в очередь.
            // Просим ровно одного и самого давнего: он ближе всех к концу
            // работы, и его потеря меньше. Просить всех значило бы обрушить
            // фоновую очередь ради одного кадра.
            if (_all.CurrentCount == 0) AskOneToYield();
            await _all.WaitAsync(ct).ConfigureAwait(false);
            LvnLaneWatch.Entered(Name, rung, waited.ElapsedMilliseconds);
            return new Pass(this, new Seat());
        }

        private void AskOneToYield()
        {
            Seat victim = null;
            lock (_seats)
                foreach (var s in _seats)
                    if (!s.Asked) { victim = s; break; }
            if (victim == null) return;
            victim.Asked = true;
            LvnLaneWatch.Yielded(Name, LvnRung.Spare);   // кто именно уступил, знает его заход
            // НАРОЧНО голый Cancel, а не дом отмены. Дом гасит И ОТПУСКАЕТ —
            // здесь отпускать рано: на этот признак подписан связанный источник
            // у захода, который сейчас и должен его услышать. Отпустив источник
            // в момент просьбы, мы обрываем провод раньше сигнала — поймано
            // прогоном 01.09 (ObjectDisposedException у двух тестов уступки).
            // Отпускает тот, кто ВОЗВРАЩАЕТ место: см. Leave.
            try { victim.Yield.Cancel(); } catch (ObjectDisposedException) { /* НАРОЧНО без Retire: отпускает Leave */ }
        }

        /// <summary>
        /// МЕСТО ВОЗВРАЩАЮТ РОВНО ОДИН РАЗ, сколько бы раз ни попросили.
        ///
        /// <para>Место — структура, и отдать её дважды легко: <c>using var</c>
        /// плюс явный <c>Dispose()</c>, копия структуры, повторный выход по
        /// ошибке. Без защиты второй возврат ШИРИТ полосу: у неё появляется
        /// место сверх объявленной ширины, и так — с каждым повтором. Это та же
        /// беда, что утечка места, только вывернутая наизнанку, и заметить её
        /// ещё труднее: ничего не зависает, просто в канал лезет больше, чем
        /// решено.</para>
        ///
        /// <para>Поймано собственным тестом 01.09: <c>SemaphoreFullException</c>
        /// на двойном возврате. Полоса имела право упасть — но правильнее не
        /// падать, а не считать второй возврат возвратом.</para>
        /// </summary>
        private void Leave(Seat seat)
        {
            if (seat == null) return;
            lock (_seats)
            {
                if (seat.Left) return;
                seat.Left = true;
                if (seat.Background) _seats.Remove(seat);
            }
            _all.Release();
            if (!seat.Background) return;
            Lvn.LvnCancel.Retire(seat.Yield);   // повторный вызов безвреден: дом это умеет
            _background.Release();
        }

        /// <summary>Занятое фоном место, которое можно попросить вернуть.</summary>
        internal sealed class Seat
        {
            public readonly CancellationTokenSource Yield = new CancellationTokenSource();

            /// <summary>Признак, снятый ОДИН РАЗ при рождении. Спрашивать его у
            /// источника каждый раз нельзя: источник отпускают при возврате
            /// места, а признак читают и после — и тогда чтение падает вместо
            /// честного «да, просили».</summary>
            public CancellationToken Token;
            public bool Asked;       // место уже просили вернуть
            public bool Background;  // фоновое: только у такого просят
            public bool Left;        // уже вернули; второй возврат — не возврат
        }

        // Занятые фоном места, в порядке занятия: первый в списке — самый давний.
        private readonly List<Seat> _seats = new List<Seat>();

        /// <summary>Место в полосе. Освобождается выходом из <c>using</c> —
        /// парного <c>Release()</c> в <c>finally</c> писать больше не нужно, и
        /// забыть его больше нельзя.</summary>
        public readonly struct Pass : IDisposable
        {
            private readonly LvnLane _lane;
            private readonly Seat _seat;
            internal Pass(LvnLane lane, Seat seat) { _lane = lane; _seat = seat; }

            /// <summary>Срабатывает, когда место просят вернуть живому. Живое
            /// место не просят никогда — у него признак пустой.</summary>
            public CancellationToken Yield
                => _seat != null && _seat.Background ? _seat.Token : CancellationToken.None;

            /// <summary>Место уже попросили вернуть. Отличать от отмены
            /// вызывающего обязан тот, кто ловит: уступка означает «зайти
            /// снова», отмена — «больше не нужно».</summary>
            public bool Yielded => _seat != null && _seat.Asked;

            public void Dispose() => _lane?.Leave(_seat);
        }
    }

    /// <summary>
    /// СТУПЕНЬ ТОГО, ЧТО СЕЙЧАС ДЕЛАЕТСЯ. Ответ на вопрос «увидят ли это
    /// сейчас» знает не загрузчик, а тот, кто его позвал: сцена ставит актёра в
    /// кадр — живое; прогрев каста набивает диск на будущее — запас.
    ///
    /// <para>Передать ступень отдельным доводом нельзя: <c>LoadSpriteAsync</c>
    /// — это дверь расширения (<c>ILvnAssets</c>), у неё восемь реализаций и
    /// сорок шесть зовущих, и менять её ради довода, который нужен трём из
    /// них, — цена не по работе. Поэтому ступень едет ОКРУЖЕНИЕМ: фоновая
    /// работа объявляет себя один раз на весь цикл, и всё, что она позовёт
    /// вглубь, наследует объявление.</para>
    ///
    /// <para><b>Умолчание — «живое».</b> Кто молчит, того считаем видимым.
    /// Объявляться обязан ФОН, и это правильная сторона: фоновых мест в движке
    /// пять и они наперечёт, а живых — весь остальной код. Забыть объявить фон
    /// — потерять бронь, а не показать пустоту.</para>
    /// </summary>
    public static class LvnRungScope
    {
        private static readonly AsyncLocal<LvnRung?> _current = new AsyncLocal<LvnRung?>();

        public static LvnRung Current => _current.Value ?? LvnRung.Live;

        /// <summary>Объявить ступень до конца <c>using</c>. Всё, что запущено
        /// внутри, наследует её — включая то, что доделается уже снаружи.</summary>
        public static Scope At(LvnRung rung) => new Scope(rung);

        /// <summary>
        /// ОБЪЯВИТЬ ЗА МОЛЧАЛИВОГО. Работа, которая живой не бывает никогда
        /// (пакетная закачка, расписание главы), ставит ступень себе сама — но
        /// только если её ещё не назвал тот, кто звал: центр загрузок вправе
        /// сказать «библиотека», и перебивать его нельзя.
        ///
        /// <para>Нужно потому, что умолчание «живое» здесь работает против
        /// себя: молчание пакетной закачки читается как «на это смотрят», она
        /// занимает бронь и не может уступить. Ровно этот разрыв и прожил
        /// полдня — ступень была объявлена СВОЕЙ полосе планировщика, у которой
        /// брони нет, а в полосу сети уходила молча.</para>
        /// </summary>
        public static Scope AtLeast(LvnRung rung)
            => new Scope(_current.Value.HasValue ? _current.Value.Value : rung);

        public readonly struct Scope : IDisposable
        {
            private readonly LvnRung? _prev;
            internal Scope(LvnRung rung) { _prev = _current.Value; _current.Value = rung; }
            public void Dispose() => _current.Value = _prev;
        }
    }

    /// <summary>Полосы движка. Ширины разные, потому что дефициты разные: сеть
    /// меряется потоками соединения, распаковка — рабочими потоками и выгрузкой
    /// в видеопамять. Общее у них одно — бронь для живого.</summary>
    public static class LvnLanes
    {
        /// <summary>СЕТЬ. HTTP/2 мультиплексирует запросы в одном соединении, так
        /// что двенадцать — это не двенадцать сокетов, а двенадцать потоков; при
        /// шести (предел HTTP/1.1) пачка мелких файлов платила лишний круг на
        /// каждый. Двое мест берегутся живому.</summary>
        public static readonly LvnLane Wire = new LvnLane("сеть", 12, 2);

        /// <summary>РАСПАКОВКА. До трёх — чтобы готовые картинки (и выгрузка в
        /// видеопамять внутри) размазывались по кадрам, а не приезжали залпом.
        /// Одно место берегётся живому: иначе иконка, которую игрок уже видит
        /// пустой, ждёт распаковки трёх фоновых.
        ///
        /// <para>ШИРИНА ЗАВИСИТ ОТ УСТРОЙСТВА, и это не украшение. Распаковка
        /// идёт на рабочих потоках Unity, а их у двухъядерного телефона ровно
        /// два — вместе с главным. Три распаковки разом означают, что главный
        /// поток стоит в очереди за собственный кадр: игрок видит рывки ровно
        /// в тот момент, когда ждёт картинку («лагает во время загрузки
        /// ассетов», живой прогон 06.09 на двух ядрах). Оставляем ядро
        /// кадру.</para></summary>
        public static readonly LvnLane Decoder =
            new LvnLane("распаковка", DecoderWidth(), 1);

        /// <summary>Ширина распаковки: на слабом устройстве — одна полоса,
        /// на обычном — прежние три. Ядер спросить не удалось (не главный
        /// поток) — берём прежнее число, чтобы не решать наугад.</summary>
        private static int DecoderWidth()
        {
            int cores = Lvn.LvnDeviceProfile.Cores;
            if (cores <= 0) return 3;
            return Mathf.Clamp(cores - 1, 1, 3);
        }
    }
}
