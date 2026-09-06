using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>Один файл контента: адрес, вид (для правил загрузки) и
    /// объявленный размер (0 — автор не назвал).</summary>
    public readonly struct LvnPart
    {
        public readonly string Url;
        public readonly string Kind;
        public readonly long Size;

        /// <summary>ОБЯЗАТЕЛЬНАЯ часть: без неё главу не начать. Скрипт и фон
        /// загрузки — всегда; ассеты — как объявил автор (<c>critical</c>).
        /// Остальное подождёт своей сцены и качается по ходу.</summary>
        public readonly bool Critical;

        public LvnPart(string url, string kind, long size = 0, bool critical = false)
        {
            Url = url; Kind = kind; Size = size; Critical = critical;
        }
    }

    /// <summary>
    /// ИЗ ЧЕГО СОСТОИТ КОНТЕНТ — единственный перечень файлов новеллы, главы и
    /// оболочки.
    ///
    /// <para>Знание «глава = скрипт + фон + объявленные ассеты» было записано
    /// ШЕСТЬ РАЗ: греем всё, планируем скачивание по главам, ставим главу в
    /// очередь, считаем «глава целиком на диске», убираем диск и оцениваем
    /// «докачать текущую». Шесть перечислений, шесть глаголов — и одно
    /// добавленное поле главы означало бы пять мест, которые о нём не узнают.
    /// Расхождение уже было: арт карточки хаба один обход брал как
    /// <c>card.image ?? cover_url</c>, а соседний — только <c>card.image</c>, и
    /// новелла без своей карточки выпадала из набора «не выгружать».</para>
    ///
    /// <para>Здесь — только ЧТО перечислять. Что с этим делать (греть, качать,
    /// проверять кэш, беречь от уборки) остаётся у вызывающего: у каждого свой
    /// глагол, но список один.</para>
    /// </summary>
    public static class LvnParts
    {
        // СЛОВАРЬ РОДОВ — один на движок. Кто определяет род по адресу —
        // DownloadPolicy.Kind; здесь только слова, чтобы производитель и
        // потребитель называли одно и то же одинаково.
        public const string Sprite = "sprite";
        public const string Script = "script";
        public const string Audio = "audio";
        /// <summary>Всё остальное: шрифт, набор, незнакомое расширение.</summary>
        public const string Bin = "bin";

        /// <summary>Файлы ОДНОЙ ГЛАВЫ.</summary>
        public static IEnumerable<LvnPart> OfChapter(LvnChapter ch)
        {
            if (ch == null) yield break;
            if (!string.IsNullOrEmpty(ch.script_url))
                yield return new LvnPart(ch.script_url, Script, 0, critical: true);
            if (!string.IsNullOrEmpty(ch.bg_url))
                yield return new LvnPart(ch.bg_url, Sprite, 0, critical: true);
            if (ch.assets == null) yield break;
            foreach (var kv in ch.assets)
                if (!string.IsNullOrEmpty(kv.Key))
                    yield return new LvnPart(kv.Key, kv.Value?.kind ?? Sprite,
                                             kv.Value?.size ?? 0, kv.Value?.critical ?? false);
        }

        /// <summary>Арт САМОЙ НОВЕЛЛЫ. Обложка и арт карточки — РАЗНЫЕ файлы,
        /// когда автор задал <c>card.image</c>: карусель рисует одно, хаб
        /// другое. Грели только обложку — карточка хаба ждала сеть уже после
        /// «всё скачано».</summary>
        public static IEnumerable<LvnPart> OfTitleArt(LvnTitle t)
        {
            if (t == null) yield break;
            if (!string.IsNullOrEmpty(t.cover_url)) yield return new LvnPart(t.cover_url, Sprite);
            var card = t.CardArt();
            if (!string.IsNullOrEmpty(card) && card != t.cover_url) yield return new LvnPart(card, Sprite);
        }

        /// <summary>Новелла целиком: её арт и все её главы.</summary>
        public static IEnumerable<LvnPart> OfTitle(LvnTitle t)
        {
            foreach (var p in OfTitleArt(t)) yield return p;
            if (t == null) yield break;
            foreach (var ch in t.ChaptersOf())
                foreach (var p in OfChapter(ch))
                    yield return p;
        }

        /// <summary>Картинки, которые рисует МЕНЮ: обложки, арт карточек новелл
        /// и коллекций, фоны глав (их показывает экран загрузки). Уборка после
        /// главы не вправе их выгружать — витрина рисует их прямо сейчас.</summary>
        public static IEnumerable<LvnPart> OfMenuArt(LvnManifest m)
        {
            if (m?.titles != null)
                foreach (var t in m.titles)
                {
                    if (t == null) continue;
                    foreach (var p in OfTitleArt(t)) yield return p;
                    foreach (var ch in t.ChaptersOf())
                        if (ch != null && !string.IsNullOrEmpty(ch.bg_url))
                            yield return new LvnPart(ch.bg_url, Sprite);
                }
            if (m?.collections == null) yield break;
            foreach (var col in m.collections)
                if (!string.IsNullOrEmpty(col?.card?.image))
                    yield return new LvnPart(col.card.image, Sprite);
        }

        /// <summary>
        /// АДРЕС, КОТОРЫЙ МОЖНО СПРОСИТЬ У СЕРВЕРА ПРЯМО СЕЙЧАС.
        ///
        /// <para>В каталоге спрайтов адреса слоёв — ШАБЛОНЫ:
        /// <c>Cold_Adele_{emotion}.png</c>, <c>..._clothes_{outfit}.png</c>.
        /// Подставляют в них значение оси в момент показа, когда известно, кто
        /// какой эмоцией стоит. Прогрев этого не знает и качать шаблон не может:
        /// файла с фигурными скобками в имени нет и быть не должно.</para>
        ///
        /// <para>Живой случай 01.09: полосу каста подключили, и она понесла на
        /// сервер 68 шаблонов из 211 слоёв — треть очереди уходила в
        /// гарантированный 404. В логе это выглядит как «сервер сломался», а
        /// сломан был СПИСОК: он просил то, чего никто не выкладывал.</para>
        /// </summary>
        private static bool Fetchable(string url)
            => !string.IsNullOrEmpty(url) && !DownloadPolicy.IsTemplate(url);

        /// <summary>
        /// КТО ГЛАВНАЯ ГЕРОИНЯ — вопрос данных, а не догадки движка.
        ///
        /// <para>Отвечает манифест: <c>ui.wardrobe.entity</c> — та, кого
        /// гардероб открывает по умолчанию. Не назвали — берём первую сущность
        /// с гардеробом, ровно как это делает сам экран гардероба. Правило уже
        /// существовало для ОТКРЫТИЯ листа и не использовалось загрузкой:
        /// качали всех подряд и последними.</para>
        /// </summary>
        public static string HeroEntity(LvnManifest m)
        {
            if (m?.sprites == null) return null;
            var named = m.ui?.wardrobe?.entity;
            if (!string.IsNullOrEmpty(named) && m.sprites.ContainsKey(named)) return named;
            foreach (var kv in m.sprites)
                if (kv.Value?.wardrobe != null && kv.Value.wardrobe.Count > 0)
                    return kv.Key;
            return null;
        }

        /// <summary>
        /// ОБЛИК ГЕРОИНИ ЦЕЛИКОМ: её эмоции (и прочие оси) плюс её гардероб.
        ///
        /// <para>Слои в каталоге — ШАБЛОНЫ (<c>hero_{emotion}.png</c>), и
        /// прогрев их пропускал: файла с фигурными скобками нет и быть не
        /// должно. Значит база с эмоциями не качалась ЗАРАНЕЕ НИКОГДА — только
        /// в момент показа. Гардероб это видел как пустые карточки.</para>
        ///
        /// <para>Здесь шаблоны разворачиваются по ОБЪЯВЛЕННЫМ осям
        /// (<c>sprites[…].axes</c>) — тем же домом подстановки, что и показ на
        /// сцене. Разворот идёт ПО ОДНОЙ ОСИ ОТ УМОЛЧАНИЙ: меняем одну ось,
        /// остальные держим по умолчанию. Иначе это произведение, а не сумма:
        /// 27 эмоций × 5 нарядов × 3 причёски — четыреста файлов вместо
        /// тридцати пяти, и «приоритет героини» превратился бы в новый способ
        /// забить очередь.</para>
        /// </summary>
        public static IEnumerable<LvnPart> OfHero(LvnManifest m)
        {
            var id = HeroEntity(m);
            if (id == null || m?.sprites == null) yield break;
            if (!m.sprites.TryGetValue(id, out var e) || e == null) yield break;

            var seen = new HashSet<string>();
            foreach (var url in HeroLooks(e, seen))
                yield return new LvnPart(url, Sprite);
            // Героиня может быть спайновой — тогда её облик это скелет, а не слои.
            foreach (var part in OfSpine(e))
                if (seen.Add(part.Url)) yield return part;

            if (e.wardrobe == null) yield break;
            foreach (var slot in e.wardrobe.Values)
            {
                if (slot == null) continue;
                if (Fetchable(slot.icon) && seen.Add(slot.icon))
                    yield return new LvnPart(slot.icon, Sprite);
                if (slot.items == null) continue;
                foreach (var it in slot.items)
                    if (Fetchable(it?.icon) && seen.Add(it.icon))
                        yield return new LvnPart(it.icon, Sprite);
            }
        }

        // Слои героини во всех объявленных состояниях: сперва умолчания (то,
        // как она выглядит по умолчанию), затем по одному значению каждой оси.
        private static IEnumerable<string> HeroLooks(LvnSpriteEntity e, HashSet<string> seen)
        {
            var defaults = e.defaults ?? new Dictionary<string, string>();
            foreach (var url in LooksAt(e, defaults, seen)) yield return url;
            if (e.axes == null) yield break;
            foreach (var axis in e.axes)
            {
                if (string.IsNullOrEmpty(axis.Key) || axis.Value == null) continue;
                foreach (var value in axis.Value)
                {
                    if (string.IsNullOrEmpty(value)) continue;
                    var state = new Dictionary<string, string>(defaults) { [axis.Key] = value };
                    foreach (var url in LooksAt(e, state, seen)) yield return url;
                }
            }
        }

        /// <summary>
        /// СКЕЛЕТ SPINE — ТОЖЕ ОБЛИК, и его до сих пор не грел никто.
        ///
        /// <para>Прогрев читал у сущности только <c>layers</c> и
        /// <c>wardrobe</c>. У спайновой сущности слоёв нет вовсе: её облик —
        /// это <c>spine.json</c>, атлас, страница атласа и (если есть)
        /// подложка. Замер 06.09 на живом каталоге: 14 спайновых сущностей,
        /// 48 файлов скелета, в ступенчатой очереди из них НОЛЬ.</para>
        ///
        /// <para>ЧЕСТНАЯ ГРАНИЦА, чтобы не приписывать себе чужой заслуги и
        /// не пугать несуществующим: ВНУТРИ ГЛАВЫ скелет греется давно —
        /// <c>PrefetchAhead</c> смотрит на двадцать пять команд вперёд и во
        /// время паузы чтения тянет спайн-сцену целиком. Дыра была там, где
        /// главы нет: меню и гардероб. Look-ahead живёт от плеера, а в меню
        /// плеер молчит — значит спайновая героиня на главном экране не
        /// грелась НИКОГДА и собиралась в тот миг, когда витрина открыта.</para>
        /// </summary>
        public static IEnumerable<LvnPart> OfSpine(LvnSpriteEntity e)
        {
            var sp = e?.spine;
            if (sp == null) yield break;
            // Порядок — от лёгкого к тяжёлому: разметка приезжает первой, и к
            // моменту, когда доедет атлас, собирать скелет уже есть из чего.
            if (Fetchable(sp.json)) yield return new LvnPart(sp.json, Sprite);
            if (Fetchable(sp.atlas)) yield return new LvnPart(sp.atlas, Sprite);
            if (Fetchable(sp.texture)) yield return new LvnPart(sp.texture, Sprite);
            if (Fetchable(sp.bg)) yield return new LvnPart(sp.bg, Sprite);
        }

        private static IEnumerable<string> LooksAt(LvnSpriteEntity e,
                                                   IReadOnlyDictionary<string, string> state,
                                                   HashSet<string> seen)
        {
            if (e.layers == null) yield break;
            foreach (var layer in e.layers)
            {
                var url = Lvn.LayerTemplate.Fill(layer?.url, state, e.defaults);
                if (Fetchable(url) && seen.Add(url)) yield return url;
            }
        }

        public static IEnumerable<LvnPart> OfCast(LvnManifest m)
        {
            if (m?.sprites == null) yield break;
            foreach (var kv in m.sprites)
            {
                var e = kv.Value;
                if (e == null) continue;
                // ШАБЛОН В ОЧЕРЕДИ — ЭТО НЕ ЗАГРУЗКА, А ГАРАНТИРОВАННЫЙ ПРОМАХ.
                //
                // Сюда клался сырой адрес слоя, а половина слоёв в каталоге
                // записана шаблонами (`hair_{hairstyle}.png`). Замер на живом
                // каталоге 06.09: 211 слоёв, из них 68 — шаблоны, и все 68
                // уходили в загрузчик как есть. Он их честно отбивал строкой
                // «ось не подставлена — в сеть не идём» (семь таких подряд в
                // журнале живого запуска), то есть персонаж не прогревался
                // вовсе, а очередь и журнал засорялись.
                //
                // Каст греется ОДНИМ обликом — тем, что по умолчанию. Разворот
                // по всем осям есть у героини (OfHero) и стоит ей ступени
                // выше; всему касту он не нужен: это сотни файлов ради чужих
                // нарядов, которых игрок может не увидеть никогда.
                if (e.layers != null)
                {
                    foreach (var layer in e.layers)
                    {
                        var url = Lvn.LayerTemplate.Fill(layer?.url, e.defaults, e.defaults);
                        // Ось, которой нет в умолчаниях, раскрыть нечем (в
                        // живом каталоге такой слой один). Молчим: заведомый
                        // 404 в очереди хуже отсутствия записи.
                        if (Fetchable(url)) yield return new LvnPart(url, Sprite);
                    }
                }
                foreach (var part in OfSpine(e))
                    yield return part;
                // Гардероб принадлежит СУЩНОСТИ, а не новелле: одну героиню
                // могут одевать в нескольких новеллах, и набор у неё один.
                if (e.wardrobe == null) continue;
                foreach (var slot in e.wardrobe.Values)
                {
                    if (slot == null) continue;
                    if (Fetchable(slot.icon))
                        yield return new LvnPart(slot.icon, Sprite);
                    if (slot.items == null) continue;
                    foreach (var it in slot.items)
                        if (!string.IsNullOrEmpty(it?.icon))
                            yield return new LvnPart(it.icon, Sprite);
                }
            }
        }

        /// <summary>Звучание ОБОЛОЧКИ: музыка витрины и звуки интерфейса.</summary>
        public static IEnumerable<LvnPart> OfShellSound(LvnManifest m)
        {
            var ui = m?.ui;
            if (ui == null) yield break;
            foreach (var url in new[] { ui.browse?.music, ui.sounds?.click, ui.sounds?.choice, ui.sounds?.type })
                if (!string.IsNullOrEmpty(url))
                    yield return new LvnPart(url, Audio);
        }

        /// <summary>Весь контент манифеста. Повторы возможны (обложка новеллы —
        /// и её часть, и картинка меню) и безвредны: их отсеет набор адресов у
        /// того, кто считает.</summary>
        public static IEnumerable<LvnPart> OfAll(LvnManifest m)
        {
            if (m?.titles != null)
                foreach (var t in m.titles)
                    foreach (var p in OfTitle(t))
                        yield return p;
            foreach (var p in OfMenuArt(m)) yield return p;
            foreach (var p in OfShellSound(m)) yield return p;
        }
    }
}
