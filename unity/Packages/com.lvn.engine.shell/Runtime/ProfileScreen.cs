using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The player PROFILE overlay — a scrim plus a scrollable sheet, themed from
    /// <see cref="LvnTokens"/> (the "Полночь" palette). It shows an identity card
    /// (avatar + name + level + XP bar), a row of stat tiles, an achievements
    /// grid, a relationships list (affection meters), and a footer with the
    /// player's UID and a copy button. Every value here ships with a hardcoded
    /// fallback so the screen renders standalone; a host wires live data by
    /// setting the public fields and calling <see cref="Rebuild"/>.
    /// </summary>
    public sealed partial class ProfileScreen : LvnOverlayScreen
    {

        /// <summary>One earned/locked achievement badge.</summary>
        public struct Achievement
        {
            public LvnIcon Icon;
            public string Title;
            public bool Unlocked;
            public Achievement(LvnIcon icon, string title, bool unlocked)
            { Icon = icon; Title = title; Unlocked = unlocked; }
        }

        /// <summary>One character relationship row (0..1 affection).</summary>
        public struct Relation
        {
            public string Name;
            public float Affection; // 0..1
            public Relation(string name, float affection)
            { Name = name; Affection = Mathf.Clamp01(affection); }
        }

        /// <summary>One stat tile: a big number over a caption.</summary>
        public struct Stat
        {
            public string Value;
            public string Caption;
            public Stat(string value, string caption)
            { Value = value; Caption = caption; }
        }

        // ── Live/overridable model (hardcoded demo fallbacks) ──────────────
        // Копия имени не хранится — см. BrowseHub: одна правда у роли
        // LvnPlayerName, экран её только показывает.

        /// <summary>TR-25: минимальный профиль — только имя и ID (уровень, XP,
        /// статы, достижения и отношения спрятаны). ui.browse.profile_full=false.</summary>
        public bool Minimal;
        public LvnIcon AvatarIcon = LvnIcon.Profile;
        public string AvatarUrl;               // optional art; falls back to the glyph
        // ЧЕГО ДВИЖОК НЕ СЧИТАЕТ, ТОГО ОН И НЕ ПОКАЗЫВАЕТ. Здесь стояли
        // «уровень 7», «1240 из 2000 XP» и чужой идентификатор — демо-значения,
        // которые никто никогда не задавал. Системы уровней в движке нет вовсе,
        // и игрок видел свой «седьмой уровень» с первого запуска: цифра из
        // воздуха читается как настоящая, потому что стоит там, где обычно
        // настоящая.
        //
        // Ноль значит «неизвестно» — блок уровня и полоса опыта не рисуются,
        // пока хост не выставит их сам (он же и ведёт счёт, если ведёт).
        public int Level;
        public int Xp;
        public int XpNext;
        public string Uid;

        // ФЕЙКА В ПРОФИЛЕ НЕТ (живой репорт): демонстрационные статы,
        // достижения и отношения удалены. Отношения — РЕАЛЬНЫЕ: хост
        // наполняет их из статов тайтлов перед открытием; пустой список
        // прячет секцию. Достижения вернутся вместе с настоящей системой.
        public List<Stat> Stats = new List<Stat>();
        public List<Achievement> Achievements = new List<Achievement>();
        public List<Relation> Relations = new List<Relation>();

        /// <summary>Реально пройдено глав по всем историям — хост считает по
        /// прогрессу перед открытием. 0 прячет плитку.</summary>
        public int ChaptersDone;

        /// <summary>Открыть экран настроек — профиль даёт на них ссылку
        /// («звук, язык, загрузка»), это ближайшее место, где их ищут.</summary>
        public System.Action OnOpenSettings;

        /// <summary>НЕ ПОКАЗЫВАЕТСЯ. Балансы живут в шапке; поле оставлено,
        /// чтобы не ломать хосты, которые его заполняют.</summary>
        public List<Stat> Wallet = new List<Stat>();

        /// <summary>«Удалить аккаунт» (стор-требование): хост стирает аккаунт
        /// на сервере и локально. true = удалено, экран закрывается; false =
        /// не вышло (нет сети), кнопка объясняет. null прячет строку.</summary>
        public Func<Task<bool>> OnDeleteAccount;

        /// <summary>«Выйти из аккаунта»: хост забывает пропуск игрока и
        /// возвращается к учётке устройства. Ничего не удаляет. null прячет
        /// строку — играм без входа она не нужна.</summary>
        public Func<Task<bool>> OnSignOut;

        private readonly ILvnAssets _assets;
        private readonly ScrollView _body;


        public ProfileScreen(ILvnAssets assets)
        {
            _assets = assets;

            // ВКЛАДКА как главная (Илья 26.08): без листа и скрима, контент на
            // общей атмосфере, дырка под нижнее меню, root не ловит тапы.
            var sheet = new VisualElement();
            ScreenUi.HubTabSheet(this, sheet);
            Add(sheet);

            // ── Top bar: back (‹) + "Профиль" ─────────────────────────────
            var top = ScreenUi.Row();
            top.style.marginBottom = LvnTokens.Space2;
            sheet.Add(top);

            var titleBlock = new VisualElement();
            titleBlock.Add(ScreenUi.Eyebrow(() => LvnWords.Of("profile.eyebrow", "PROFILE")));
            var title = SectionTitle(() => LvnWords.Of("profile.title", "Profile"));
            titleBlock.Add(title);
            top.Add(titleBlock);

            // ── Scrollable body ───────────────────────────────────────────
            _body = Lvn.UI.LvnScroll.Vertical();
            _body.style.flexGrow = 1;
            sheet.Add(_body);

            Rebuild();
        }

        // Тело собирается на КАЖДОМ открытии: поля (Minimal, Relations)
        // хост ставит после конструктора — снимок из конструктора показывал
        // бы вечную заглушку (класс бага «пустых настроек», зеркальный).
        protected override void OnOpening() => Rebuild();

        /// <summary>Слова, шрифт или размеры сменились — перечитать их.</summary>

        public override void Rebuild()
        {
            _body.Clear();
            _body.Add(BuildIdentityCard());
            if (!Minimal && Stats.Count > 0) _body.Add(BuildStatRow());

            if (ChaptersDone > 0) _body.Add(ProgressLine());

            // КОШЕЛЬКА ЗДЕСЬ НЕТ. Балансы живут в шапке — она видна всегда и
            // обновляется сама; вторая копия в профиле показывала те же числа
            // с задержкой на открытие экрана и расходилась с шапкой ровно в тот
            // момент, когда игрок сверял их глазами (решение Ильи, 28.08).

            if (!Minimal && Achievements.Count > 0)
            {
                _body.Add(ScreenUi.SectionHeader(LvnWords.Of("profile.achievements", "Achievements")));
                _body.Add(BuildAchievements());
            }

            // Отношения с фаворитами — реальные данные, показываются и в
            // минимальном профиле: это то, ради чего игрок сюда заходит.
            // Пустоту не прячем, а объясняем: игрок должен знать, что здесь
            // вырастет и от чего (живой репорт «там пустота, смысл какой»).
            _body.Add(ScreenUi.SectionHeader(LvnWords.Of("profile.relations", "Relationships")));
            if (Relations.Count > 0) _body.Add(BuildRelations());
            else _body.Add(HintCard(
                LvnWords.Of("profile.relations_empty", "The first choice already bends the story. Start a chapter and your ties appear here.")));

            if (OnOpenSettings != null) _body.Add(SettingsLink());
            if (OnSignOut != null) _body.Add(SignOutRow());
            if (OnDeleteAccount != null) _body.Add(DeleteAccountRow());

            _body.Add(BuildFooter());
        }

        // Честная строка прогресса: единственная цифра, которую профиль
        // может показать без выдумок на любом аккаунте.
        private VisualElement ProgressLine()
        {
            var row = LvnStyler.CardRow(ScreenUi.Row());
            LvnAir.PadX(row, LvnTokens.Space3);   // поля по горизонтали — у экрана
            row.style.marginBottom = LvnTokens.Space2;
            var ic = LvnIcons.Make(LvnIcon.Book, 22f, LvnTokens.Accent);
            ic.style.marginRight = LvnTokens.Space2;
            row.Add(ic);
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("profile.chapters_read", "Chapters read: {0}",
                                            $"{ChaptersDone} {ChapterWord(ChaptersDone)}"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = LvnTokens.TextSm;
            row.Add(lbl);
            return row;
        }

        // Мягкая карточка-пояснение вместо пустого места.
        private VisualElement HintCard(string text)
        {
            var card = new VisualElement();
            LvnChrome.Card(card, LvnTokens.SurfaceSoft);
            LvnAir.Pad(card, LvnTokens.Space3);
            card.style.marginBottom = LvnTokens.Space2;
            var lbl = new Label(text);
            ScreenUi.Quiet(lbl, LvnTokens.TextSm);
            card.Add(lbl);
            return card;
        }


        // ── Section 2: identity card ───────────────────────────────────────
        private VisualElement BuildIdentityCard()
        {
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Column;
            LvnChrome.Card(card, LvnTokens.SurfaceHi, LvnTokens.Radius);
            LvnAir.Pad(card, LvnTokens.Space3);
            card.style.marginBottom = LvnTokens.Space3;

            var dossier = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("profile.dossier", "STORY RECORD"));
            dossier.style.color = LvnTokens.Gold;
            dossier.style.fontSize = LvnTokens.TextMicro;
            dossier.style.letterSpacing = 1.9f;
            dossier.style.unityFontStyleAndWeight = FontStyle.Bold;
            dossier.style.marginBottom = LvnTokens.Space2;
            card.Add(dossier);

            var identity = ScreenUi.Row();
            card.Add(identity);

            // Circular avatar with an Accent ring.
            const float avatarSize = 96f;
            var avatar = new VisualElement();
            avatar.style.width = avatarSize;
            avatar.style.height = avatarSize;
            avatar.style.marginRight = LvnTokens.Space3;
            avatar.style.alignItems = Align.Center;
            avatar.style.justifyContent = Justify.Center;
            avatar.style.backgroundColor = LvnTokens.SurfaceHi;
            LvnChrome.Frame(avatar, avatarSize / 2f, LvnTokens.Accent, 3f);

            var glyph = LvnIcons.Make(AvatarIcon, 50f, LvnTokens.Text);
            glyph.style.alignSelf = Align.Center;
            avatar.Add(glyph);
            if (!string.IsNullOrEmpty(AvatarUrl))
            {
                LvnPicture.Photo(avatar, AvatarUrl, _assets);
            }
            identity.Add(avatar);

            // Name + level + XP.
            var col = new VisualElement();
            col.style.flexGrow = 1;
            identity.Add(col);

            var name = new Label(Lvn.UI.LvnPlayerName.Display);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextLg;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            col.Add(name);

            if (Minimal) return card; // TR-25: профиль = имя + ID, без уровня и XP
            // Уровня нет — секции нет: пустая полоса опыта врёт не меньше
            // выдуманной, а «Уровень 0» выглядит поломкой.
            if (Level <= 0 && XpNext <= 0) return card;

            var level = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("profile.level", "Level {0}", Level));
            level.style.color = LvnTokens.Accent;
            level.style.fontSize = LvnTokens.TextSm;
            LvnAir.MarginY(level, LvnTokens.Hair, LvnTokens.Space2);
            col.Add(level);

            // XP progress: a track with an Accent fill.
            int next = XpNext > 0 ? XpNext : 1;
            float frac = Mathf.Clamp01((float)Xp / next);

            col.Add(LvnStyler.Bar(16f, frac, LvnTokens.SurfaceHi));

            var xpLabel = new Label($"{LvnPriceTag.Amount(Xp)} / {LvnPriceTag.Amount(next)} XP");
            xpLabel.style.color = LvnTokens.TextDim;
            xpLabel.style.fontSize = LvnTokens.TextXs;
            xpLabel.style.marginTop = LvnTokens.Space1;
            col.Add(xpLabel);

            return card;
        }

        // ── Section 3: stat tiles ──────────────────────────────────────────
        private VisualElement BuildStatRow() => TileRow(Stats);

        private VisualElement TileRow(List<Stat> stats)
        {
            var row = new VisualElement();
            LvnFlow.Wrap(row, Justify.SpaceBetween);
            row.style.marginBottom = LvnTokens.Space1;

            foreach (var s in stats) row.Add(StatTile(s));
            return row;
        }



        private VisualElement StatTile(Stat s)
        {
            var tile = new VisualElement();
            tile.style.flexGrow = 1;
            tile.style.flexBasis = Length.Percent(22f);
            tile.style.minWidth = 120;
            tile.style.marginBottom = LvnTokens.Space2;
            tile.style.marginRight = LvnTokens.Space1;
            tile.style.alignItems = Align.Center;
            LvnChrome.Card(tile);
            LvnAir.Pad(tile, LvnTokens.Space1, LvnTokens.Space3);

            var value = new Label(s.Value);
            value.style.color = LvnTokens.Gold;
            value.style.fontSize = LvnTokens.TextLg;
            value.style.unityFontStyleAndWeight = FontStyle.Bold;
            tile.Add(value);

            var caption = new Label(s.Caption);
            caption.style.color = LvnTokens.TextDim;
            caption.style.fontSize = LvnTokens.TextXs;
            caption.style.marginTop = LvnTokens.Tight;
            caption.style.whiteSpace = WhiteSpace.Normal;
            caption.style.unityTextAlign = TextAnchor.MiddleCenter;
            tile.Add(caption);

            return tile;
        }

        // ── Section 4: achievements grid ───────────────────────────────────
        private VisualElement BuildAchievements()
        {
            var grid = new VisualElement();
            LvnFlow.Wrap(grid, Justify.FlexStart);
            grid.style.marginBottom = LvnTokens.Space1;

            foreach (var a in Achievements) grid.Add(Badge(a));
            return grid;
        }

        private VisualElement Badge(Achievement a)
        {
            var badge = new VisualElement();
            badge.style.flexBasis = Length.Percent(23f);
            badge.style.flexGrow = 1;
            badge.style.minWidth = 110;
            badge.style.marginRight = LvnTokens.Space1;
            badge.style.marginBottom = LvnTokens.Space1;
            badge.style.alignItems = Align.Center;
            // Единственный ярлык, у которого боковой отступ МЕНЬШЕ
            // вертикального: значок стоит в плотной сетке достижений.
            LvnStyler.Chip(badge, a.Unlocked ? LvnTokens.SurfaceHi : LvnTokens.Surface,
                           padX: LvnTokens.Space1, padY: LvnTokens.Space2);
            if (!a.Unlocked) badge.style.opacity = 0.55f;

            var icon = LvnIcons.Make(a.Unlocked ? a.Icon : LvnIcon.Lock, 32f,
                                     a.Unlocked ? LvnTokens.Accent : LvnTokens.TextDim,
                                     0f, a.Unlocked ? LvnTheme.Current.IconGlow : 0f);
            icon.style.alignSelf = Align.Center;
            badge.Add(icon);

            var label = new Label(a.Title);
            label.style.color = a.Unlocked ? LvnTokens.Text : LvnTokens.TextDim;
            label.style.fontSize = LvnTokens.TextXs;
            label.style.marginTop = LvnTokens.Space1;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.Add(label);

            return badge;
        }

        // ── Section 5: relationships ───────────────────────────────────────
        private VisualElement BuildRelations()
        {
            var list = new VisualElement();
            list.style.marginBottom = LvnTokens.Space1;
            foreach (var r in Relations) list.Add(RelationRow(r));
            return list;
        }

        private VisualElement RelationRow(Relation r)
        {
            var row = new VisualElement();
            LvnChrome.Card(row);
            LvnAir.Pad(row, LvnTokens.Space3, LvnTokens.Space2);
            row.style.marginBottom = LvnTokens.Space2;

            var head = ScreenUi.Row(spread: true);
            head.style.marginBottom = LvnTokens.Space1;
            row.Add(head);

            var nameRow = ScreenUi.Row();
            var heart = LvnIcons.Make(LvnIcon.Heart, 20f, LvnTokens.Accent);
            heart.style.marginRight = LvnTokens.Space1;
            nameRow.Add(heart);
            var name = new Label(r.Name);
            name.style.color = LvnTokens.Text;
            name.style.fontSize = LvnTokens.TextSm;
            nameRow.Add(name);
            head.Add(nameRow);

            var pct = new Label($"{Mathf.RoundToInt(r.Affection * 100f)}%");
            pct.style.color = LvnTokens.Accent;
            pct.style.fontSize = LvnTokens.TextSm;
            pct.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.Add(pct);

            row.Add(LvnStyler.Bar(14f, r.Affection, LvnTokens.SurfaceHi));

            return row;
        }



        // Склонение держит словарь: правило зависит от языка, а не от экрана.
        private static string ChapterWord(int count)
            => LvnWords.Plural("chapter", count, "chapter", "chapters");

        // Жизненный цикл накладного экрана — в базовом классе
        // (LvnOverlayScreen): проявление, ожидание, угасание и отмена открытия
        // из Hide() одинаковы у всех восьми экранов оболочки.

    }
}
