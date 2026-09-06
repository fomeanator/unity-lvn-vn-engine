using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lvn.Content
{
    /// <summary>
    /// КТО СЧИТАЕТ ДЕРЖАТЕЛЕЙ СПРАЙТА. Загрузчику этот дом не нужен — он и
    /// есть счётчик; дом нужен ТОМУ, КТО ДЕРЖИТ: доске закрепления в слое
    /// интерфейса. Ей от загрузчика требуется ровно одно действие, и знать
    /// про кэш, стриминговое окно и отставленные записи она не должна.
    ///
    /// <para>Побочно это делает саму доску проверяемой: подставной счётчик
    /// отвечает на единственный вопрос, ради которого доска существует, —
    /// доходил ли общий спрайт до нуля держателей хоть на мгновение.</para>
    /// </summary>
    public interface ILvnPinLedger
    {
        /// <summary>Плюс или минус один держатель. Уравновешено: каждому
        /// <c>true</c> положен свой <c>false</c>.</summary>
        void PinSprite(UnityEngine.Sprite sprite, bool pinned);
    }

    /// <summary>
    /// ПАМЯТЬ ПОД КАРТИНКИ — сколько держим, что вытесняем и чего не трогаем.
    ///
    /// <para>Отдельным домом, потому что это бухгалтерия, а не загрузка: у неё
    /// свои правила и своя цена ошибки. Здесь живут бюджет по устройству,
    /// давность запроса, вытеснение старейшего и — главное — ПИН: пока сцена
    /// держит спрайт, его нельзя уничтожить никаким путём.</para>
    ///
    /// <para>Это правило стоило недели дефектов. LRU его соблюдал всегда
    /// («never evict pinned»), а выгрузка по адресу — нет: живое обновление
    /// контента звало <c>Unload</c>, и текстуры умирали прямо на экране —
    /// героиня оставалась стоять пустыми слоями, и починить это было нечем,
    /// потому что арт уничтожили под ней (живой лог Ильи 28.08). Теперь
    /// закреплённая запись уходит ИЗ КЭША, но не из памяти: она ждёт в
    /// стороне, пока последний державший её не отпустит.</para>
    /// </summary>
    public sealed partial class ContentLoader
    {
        /// <summary>
        /// СКОЛЬКО ПАМЯТИ ОТДАТЬ ПОД КАРТИНКИ — одно правило, отдельно от того,
        /// кто его применяет.
        ///
        /// <para>Шестая часть памяти устройства — старая мерка, годная для
        /// телефона, который наш целиком. Рядом, однако, живут система,
        /// магазин и лаунчер, и на устройстве с 976 МБ свободными оставались
        /// 80 при объявленном бюджете 162 МБ (живой замер 06.09). Поэтому
        /// решает МЕНЬШЕЕ из двух: доля устройства и половина планки, которую
        /// Android обещает приложению.</para>
        ///
        /// <para>Планка неизвестна (редактор, другая платформа) — работает
        /// прежнее правило с полом в 96 МБ. Планка известна и мала — пол
        /// опускается вместе с ней: невыполнимый бюджет хуже маленького.</para>
        /// </summary>
        internal static long BudgetFor(int ramMb, int heapMb)
        {
            long b = ((long)ramMb << 20) / 6;
            long floor = 96L << 20;
            if (heapMb > 0)
            {
                long half = ((long)heapMb << 20) / 2;
                b = Math.Min(b, half);
                floor = Math.Min(floor, half);
            }
            return Math.Max(floor, Math.Min(384L << 20, b));
        }

        private static void TuneBudgetForDevice()
        {
            if (_budgetTuned) return;
            _budgetTuned = true;
            int mb = Lvn.LvnDeviceProfile.RamMb;
            if (mb <= 0) return; // тесты/неизвестное устройство — дефолт
            int heap = Lvn.LvnDeviceProfile.HeapLimitMb;
            long clamped = BudgetFor(mb, heap);
            if (clamped != SpriteCacheBudgetBytes)
            {
                SpriteCacheBudgetBytes = clamped;
                LvnLog.Info($"[lvn-content] бюджет спрайт-кэша: {clamped >> 20} МБ "
                          + $"(RAM устройства {mb} МБ, планка приложения "
                          + (heap > 0 ? $"{heap} МБ)" : "неизвестна)"));
            }
        }

        // Система кричит «мало памяти» — сбрасываем незапиненный декод до
        // половины бюджета, невзирая на grace. Диск-кэш цел: всё вернётся
        // ре-декодом по мере надобности. Раньше сигнал не слушался вовсе.
        private void OnLowMemory()
        {
            List<SpriteEntry> victims;
            lock (_spriteCache)
                victims = EvictToLocked(SpriteCacheBudgetBytes / 2, graceSeconds: 0f);
            foreach (var v in victims) DestroySprite(v.Sprite);
            Debug.LogWarning($"[lvn-content] lowMemory: сброшено {victims.Count} спрайтов, живо {_spriteBytes >> 20} МБ");
        }

        internal Sprite CacheSpriteForTest(string url, Sprite sprite, long bytes)
            => CacheSprite(url, sprite, bytes);

        internal Sprite CachedSpriteForTest(string url)
        {
            lock (_spriteCache)
                return _spriteCache.TryGetValue(url, out var e) ? e.Sprite : null;
        }

        private Sprite CacheSprite(string url, Sprite sprite, long bytes)
        {
            // Честная бухгалтерия вместо оценок вызывающих: w*h*4 не считал
            // мип-цепочку (+33% у всего крупного арта), а KTX2-путь записывал
            // размер ФАЙЛА вместо транскода в видеопамяти (×5 недоучёт).
            // Профайлер знает настоящий размер текстуры в рантайме.
            var tex = sprite != null ? sprite.texture : null;
            if (tex != null)
            {
                long honest = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
                if (honest > 0) bytes = honest;
            }
            List<SpriteEntry> victims;
            lock (_spriteCache)
            {
                // ТОТ ЖЕ URL УЖЕ МОГ ЛЕЖАТЬ В КЭШЕ (гонка двух декодов, живое
                // обновление файла). Простая перезапись словаря теряла старую
                // запись целиком: её байты навсегда оставались в учёте, а её
                // ПИНЫ исчезали — сцена держала спрайт, о котором кэш больше
                // не знал. Проводим прежнюю запись по правилам: закреплённая
                // уходит в сторону и живёт, пока её держат.
                if (_spriteCache.TryGetValue(url, out var old) && !ReferenceEquals(old.Sprite, sprite))
                {
                    _spriteBytes -= old.Bytes;
                    RetireLocked(old);
                }
                var e = new SpriteEntry { Sprite = sprite, Bytes = bytes };
                Touch(e);
                _spriteCache[url] = e;
                _spriteBytes += e.Bytes;
                victims = EvictToLocked(SpriteCacheBudgetBytes, SpriteEvictionGraceSeconds);
            }
            FlushDestroys();
            foreach (var v in victims) DestroySprite(v.Sprite);
            return sprite;
        }

        private void Touch(SpriteEntry e)
        {
            e.Seq = ++_spriteSeq;
            // РЕАЛЬНОЕ время, а не общие часы интерфейса (LvnClock): давность
            // спрайта должна расти и пока игра свёрнута — иначе после возврата
            // весь кэш выглядит «только что использованным» и вытеснять нечего.
            e.At = Lvn.LvnClock.Wall();
        }

        // Must run under the _spriteCache lock. Returns the evicted entries so the
        // caller destroys their textures OUTSIDE the lock.
        private List<SpriteEntry> EvictToLocked(long budgetBytes, float graceSeconds)
        {
            var victims = new List<SpriteEntry>();
            if (_spriteBytes <= budgetBytes) return victims;
            float now = Lvn.LvnClock.Wall();
            foreach (var url in PickEvictions(
                         SnapshotLocked(), budgetBytes, now, graceSeconds))
            {
                if (!_spriteCache.TryGetValue(url, out var e)) continue;
                DropLocked(url, e);
                victims.Add(e);
            }
            return victims;
        }

        /// <summary>СНЯТЬ ЗАПИСЬ С УЧЁТА — из карты и из счёта байтов разом.
        ///
        /// <para>Два действия, и они обязаны идти вместе: карта отвечает «что у
        /// нас есть», счётчик — «сколько это весит», а решение о вытеснении
        /// принимается по СЧЁТЧИКУ. Забудь вычесть — бюджет считает память
        /// занятой, и кэш выбрасывает живое; вычти дважды — считает свободной,
        /// и растёт до отказа. Ни то, ни другое не даёт ошибки: игра просто
        /// перезагружает картинки или падает по памяти.</para>
        ///
        /// <para>Обряд стоял тремя копиями, и в РАЗНЫХ ПОРЯДКАХ — где-то сперва
        /// вычитали, где-то сперва удаляли. Порядок здесь безразличен (всё под
        /// одним замком), но разные написания одного действия — верный признак,
        /// что четвёртое напишут без половины.</para>
        ///
        /// <para>Что делать с самой записью — НЕ здесь: закреплённая уходит в
        /// сторону и живёт, пока её держат; выселенную уничтожают снаружи
        /// замка. Это решение места.</para>
        /// </summary>
        private void DropLocked(string url, SpriteEntry e)
        {
            _spriteCache.Remove(url);
            _spriteBytes -= e.Bytes;
        }

        private List<(string url, long bytes, long seq, float at, bool pinned)> SnapshotLocked()
        {
            var list = new List<(string, long, long, float, bool)>(_spriteCache.Count);
            foreach (var kv in _spriteCache)
                list.Add((kv.Key, kv.Value.Bytes, kv.Value.Seq, kv.Value.At, kv.Value.Pins > 0));
            return list;
        }

        /// <summary>Pure eviction policy, exposed for tests: evict oldest-requested
        /// first until the total fits the budget, skipping anything requested within
        /// the grace window (it's very likely still on screen) or pinned (a live
        /// consumer, e.g. a built Spine skeleton, still references its texture).</summary>
        internal static List<string> PickEvictions(
            List<(string url, long bytes, long seq, float at, bool pinned)> entries,
            long budgetBytes, float now, float graceSeconds)
        {
            var evict = new List<string>();
            long total = 0;
            foreach (var e in entries) total += e.bytes;
            if (total <= budgetBytes) return evict;
            entries.Sort((a, b) => a.seq.CompareTo(b.seq)); // oldest request first
            foreach (var e in entries)
            {
                if (total <= budgetBytes) break;
                if (e.pinned) continue;                     // in use by a live skeleton — never evict
                if (now - e.at < graceSeconds) continue;    // recently used — protected
                evict.Add(e.url);
                total -= e.bytes;
            }
            // Grace — вежливость, а не вето. Загрузка главы трогает ВСЁ за
            // минуту, и «свежее не вытесняем» означало «бюджет не работает
            // ровно тогда, когда нужен». Всё ещё над бюджетом — вытесняем и
            // свежие (кроме запиненных), по-прежнему старейшие сначала.
            if (total > budgetBytes)
            {
                foreach (var e in entries)
                {
                    if (total <= budgetBytes) break;
                    if (e.pinned || evict.Contains(e.url)) continue;
                    evict.Add(e.url);
                    total -= e.bytes;
                }
            }
            return evict;
        }

        /// <summary>Pin/unpin the cache entry backing <paramref name="sprite"/> so
        /// the LRU never destroys a texture still in use by a live consumer — a
        /// built Spine skeleton whose atlas/material references these page textures
        /// (an evicted page turns the skeleton black/pink with no way to recover
        /// short of a full rebuild). Balanced: each Pin(true) needs a Pin(false).
        /// No-op if the sprite isn't cached (already gone / never went through here).</summary>
        public void PinSprite(Sprite sprite, bool pinned)
        {
            if (sprite == null) return;
            SpriteEntry freed = null;
            lock (_spriteCache)
            {
                foreach (var e in _spriteCache.Values)
                    if (ReferenceEquals(e.Sprite, sprite))
                    {
                        e.Pins += pinned ? 1 : -1;
                        if (e.Pins < 0) e.Pins = 0;
                        return;
                    }
                // ОТСТАВЛЕННЫЕ ТОЖЕ СЧИТАЮТСЯ. Спрайт мог покинуть кэш, пока
                // его держала сцена (обновление контента, смена качества): он
                // ждёт в стороне, и уничтожить его можно ровно тогда, когда
                // последний державший отпустит. Без этого пин «повисал» —
                // текстура жила бы вечно, а сцене вернули бы пустоту.
                for (int i = _retired.Count - 1; i >= 0; i--)
                {
                    var e = _retired[i];
                    if (!ReferenceEquals(e.Sprite, sprite)) continue;
                    e.Pins += pinned ? 1 : -1;
                    if (e.Pins <= 0) { freed = e; _retired.RemoveAt(i); }
                    break;
                }
            }
            if (freed != null) DestroySprite(freed.Sprite);
        }

        // Спрайты, ушедшие из кэша, пока их ДЕРЖАЛА сцена. Они не в кэше (новая
        // загрузка того же url даст свежий арт) и не уничтожены (иначе живая
        // картинка на экране превратилась бы в пустой прямоугольник).
        private readonly List<SpriteEntry> _retired = new List<SpriteEntry>();

        /// <summary>
        /// ПРОВОДИТЬ ЗАПИСЬ ИЗ КЭША. Закреплённую — в сторону, незакреплённую —
        /// в утиль.
        ///
        /// <para>Это и был корень «белого прямоугольника вместо героини»:
        /// живое обновление контента звало <see cref="Unload"/>, а тот, в
        /// отличие от LRU, про пины не знал вовсе — и уничтожал текстуры,
        /// которые в этот момент рисовались на экране (живой лог Ильи 28.08:
        /// «content changed — reloading», следом «у снимка отобрали текстуры»
        /// и пять погашенных слоёв). Освобождать память нужно, но не из-под
        /// того, кто ей прямо сейчас пользуется.</para>
        /// </summary>
        private void RetireLocked(SpriteEntry entry)
        {
            if (entry == null) return;
            if (entry.Pins > 0) _retired.Add(entry);
            else _pendingDestroy.Add(entry);
        }

        // Уничтожать под замком нельзя (Unity-объекты + чужие локи), поэтому
        // жертвы копятся здесь и уходят сразу после выхода из lock.
        private readonly List<SpriteEntry> _pendingDestroy = new List<SpriteEntry>();

        private void FlushDestroys()
        {
            List<SpriteEntry> gone = null;
            lock (_spriteCache)
            {
                if (_pendingDestroy.Count == 0) return;
                gone = new List<SpriteEntry>(_pendingDestroy);
                _pendingDestroy.Clear();
            }
            foreach (var e in gone) DestroySprite(e.Sprite);
        }

        /// <summary>Releases the in-memory sprite cached for a single url and
        /// destroys its texture. Safe to call if the url was never loaded. The
        /// disk cache is left intact (a later load re-decodes from disk).</summary>
        public void Unload(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            lock (_spriteCache)
            {
                if (!_spriteCache.TryGetValue(url, out var entry)) return;
                DropLocked(url, entry);
                RetireLocked(entry);   // закреплённое переживёт выгрузку
            }
            FlushDestroys();
        }

        /// <summary>Releases every cached sprite whose url matches — e.g. a chapter's
        /// art/backgrounds on chapter exit, keeping UI covers/skins warm.</summary>
        public void UnloadWhere(Func<string, bool> match)
        {
            if (match == null) return;
            lock (_spriteCache)
            {
                var keys = new List<string>(_spriteCache.Keys);
                foreach (var k in keys)
                {
                    if (!match(k)) continue;
                    var e = _spriteCache[k];
                    DropLocked(k, e);
                    RetireLocked(e);   // то, что на экране, не убиваем
                }
            }
            FlushDestroys();
        }

        /// <summary>Releases every in-memory sprite and destroys its texture. Call
        /// on a scene transition or app exit to free GPU memory. The disk cache is
        /// untouched.</summary>
        public void UnloadAll()
        {
            lock (_spriteCache)
            {
                foreach (var e in _spriteCache.Values) RetireLocked(e);
                _spriteCache.Clear();
                _spriteBytes = 0;
            }
            FlushDestroys();
        }

        private static void DestroySprite(Sprite sprite)
        {
            if (sprite == null) return;
            if (sprite.texture != null) UnityEngine.Object.Destroy(sprite.texture);
            UnityEngine.Object.Destroy(sprite);
        }
    }
}
