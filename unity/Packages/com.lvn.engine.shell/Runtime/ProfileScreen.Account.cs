using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// АККАУНТ В ПРОФИЛЕ — часть <see cref="ProfileScreen"/>: идентификатор
    /// игрока, ссылка в настройки и удаление аккаунта со взведённым
    /// подтверждением (стор-требование).
    /// </summary>
    public sealed partial class ProfileScreen
    {
        // Ссылка на настройки: звук/язык/загрузку ищут в профиле — дадим путь.
        private VisualElement SettingsLink()
        {
            var row = LvnStyler.CardRow(ScreenUi.Row(spread: true), LvnTokens.SurfaceSoft);
            LvnAir.PadX(row, LvnTokens.Space3);
            LvnAir.MarginY(row, LvnTokens.Space1, LvnTokens.Space2);
            var col = new VisualElement();
            col.style.flexGrow = 1;
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("settings.title", "Settings"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = LvnTokens.TextSm;
            col.Add(lbl);
            var hint = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("settings.hint", "Sound, story language and full download"));
            hint.style.color = LvnTokens.TextDim;
            hint.style.fontSize = LvnTokens.TextXs;
            hint.style.marginTop = LvnTokens.Hair;
            col.Add(hint);
            row.Add(col);
            var arrow = new Label("›");
            arrow.style.color = LvnTokens.Accent;
            arrow.style.fontSize = LvnTokens.TextBase;
            arrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(arrow);
            row.RegisterCallback<ClickEvent>(_ => { Close(); OnOpenSettings?.Invoke(); });
            return row;
        }

        // «ВЫЙТИ ИЗ АККАУНТА». До 06.09 выхода не было вовсе, и его роль
        // случайно исполняла регистрация при старте: вход игрока не переживал
        // закрытия игры. Теперь вход держится — значит выход обязан быть
        // явным, иначе телефон, на котором кто-то вошёл своей учёткой,
        // невозможно вернуть хозяину.
        private VisualElement SignOutRow()
        {
            var row = LvnStyler.CardRow(ScreenUi.Row(spread: true), LvnTokens.SurfaceSoft);
            LvnAir.PadX(row, LvnTokens.Space3);
            row.style.marginBottom = LvnTokens.Space2;

            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;
            col.style.marginRight = LvnTokens.Space2;
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("account.sign_out", "Sign out"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = LvnTokens.TextSm;
            col.Add(lbl);
            var hint = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("account.sign_out_hint", "Back to this device's account. Nothing is deleted."));
            hint.style.color = LvnTokens.TextDim;
            hint.style.fontSize = LvnTokens.TextXs;
            hint.style.marginTop = LvnTokens.Hair;
            hint.style.whiteSpace = WhiteSpace.Normal;
            col.Add(hint);
            row.Add(col);

            var btn = new Button();
            btn.style.fontSize = LvnTokens.TextXs;
            LvnAir.Pad(btn, LvnTokens.Space3, LvnTokens.Space2);
            LvnStyler.Plate(btn, LvnTokens.Faint, LvnTokens.Text, LvnTokens.RadiusSm);
            // Выход ничего не стирает, поэтому переспроса не заслуживает —
            // в отличие от удаления, которое рядом и необратимо.
            Lvn.UI.LvnRedress.Bind(btn, () => LvnWords.Of("account.sign_out_do", "Sign out"));
            btn.clicked += () =>
            {
                btn.SetEnabled(false);
                LvnAsync.Fire(RunSignOutAsync(btn), "SignOut");
            };
            row.Add(btn);
            return row;
        }

        private async Task RunSignOutAsync(Button btn)
        {
            bool ok = false;
            try { ok = await OnSignOut(); }
            catch (Exception e) { Debug.LogWarning($"[lvn-profile] выход: {e.Message}"); }
            if (ok) { Close(); return; }
            btn.SetEnabled(true);
            LvnMotion.FlashText(btn, Lvn.Content.LvnOfflineText.TryLater, LvnMotion.NoticeLong);
        }

        // «Удалить аккаунт»: приглушённая строка с подтверждением в два нажатия
        // прямо в кнопке — отдельный диалог тут был бы тяжелее самого действия.
        private VisualElement DeleteAccountRow()
        {
            var row = LvnStyler.CardRow(ScreenUi.Row(spread: true));
            LvnAir.PadX(row, LvnTokens.Space3);
            row.style.marginBottom = LvnTokens.Space2;

            var col = new VisualElement();
            col.style.flexGrow = 1;
            col.style.flexShrink = 1;
            col.style.marginRight = LvnTokens.Space2;
            var lbl = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("account.delete", "Delete account"));
            lbl.style.color = LvnTokens.Text;
            lbl.style.fontSize = LvnTokens.TextSm;
            col.Add(lbl);
            var hint = Lvn.UI.LvnRedress.Bind(new Label(), () => LvnWords.Of("account.delete_hint", "Erases progress, purchases and saves. Forever."));
            hint.style.color = LvnTokens.TextDim;
            hint.style.fontSize = LvnTokens.TextXs;
            hint.style.marginTop = LvnTokens.Hair;
            hint.style.whiteSpace = WhiteSpace.Normal;
            col.Add(hint);
            row.Add(col);

            var danger = LvnTokens.Bad;
            // НАДПИСЬ ЧИТАЕТ СОСТОЯНИЕ, А НЕ НАЗНАЧАЕТСЯ ПО ШАГАМ. Со сменой
            // языка привязка перечитывает источник: назначь надпись руками — и
            // взведённая кнопка вернула бы вид «Удалить», оставшись взведённой.
            // Следующее нажатие удалило бы аккаунт без переспроса.
            var btn = new Button();
            btn.style.fontSize = LvnTokens.TextXs;
            LvnAir.Pad(btn, LvnTokens.Space3, LvnTokens.Space2);
            LvnStyler.Plate(btn, LvnTokens.Faint, danger, LvnTokens.RadiusSm);
            // Обряд целиком — у дома (Lvn.UI.LvnAskTwice). Здесь остаётся
            // только ВИД опасности (заливка на время взвода) и само действие.
            Lvn.UI.LvnAskTwice.AskTwice(btn,
                calm: () => LvnWords.Of("account.delete_do", "Delete"),
                armed: () => LvnWords.Of("account.delete_sure", "Really delete?"),
                confirmed: () =>
                {
                    btn.SetEnabled(false);
                    btn.text = LvnWords.Of("account.deleting", "Deleting…");
                    LvnAsync.Fire(RunDeleteAsync(btn, danger), "DeleteAccount");
                },
                armedTint: danger);
            row.Add(btn);
            return row;
        }

        private async Task RunDeleteAsync(Button btn, Color danger)
        {
            bool ok = false;
            try { ok = await OnDeleteAccount(); }
            catch (Exception e) { Debug.LogWarning($"[lvn-profile] удаление аккаунта: {e.Message}"); }
            if (ok) { Close(); return; }
            btn.SetEnabled(true);
            btn.style.backgroundColor = LvnTokens.Faint;
            btn.style.color = danger;
            btn.text = LvnWords.Of("account.delete_do", "Delete");
            LvnMotion.FlashText(btn, Lvn.Content.LvnOfflineText.TryLater, LvnMotion.NoticeLong);
        }

        // ── Section 6: footer (UID + copy) ─────────────────────────────────
        private VisualElement BuildFooter()
        {
            var footer = ScreenUi.Row(spread: true);
            footer.style.marginTop = LvnTokens.Space1;
            footer.style.paddingTop = LvnTokens.Space2;
            LvnChrome.Divider(footer, LvnSide.Top);

            var id = string.IsNullOrEmpty(Uid) ? "u_unknown" : Uid;
            var idLabel = new Label($"ID: {Shorten(id)}");
            idLabel.style.color = LvnTokens.TextDim;
            idLabel.style.fontSize = LvnTokens.TextXs;
            idLabel.style.flexGrow = 1;
            footer.Add(idLabel);

            var copy = Lvn.UI.LvnRedress.Bind(new Button(), () => LvnWords.Of("settings.copy", "Copy"));
            copy.style.fontSize = LvnTokens.TextXs;
            LvnAir.Pad(copy, LvnTokens.Space3, LvnTokens.Space2);
            LvnStyler.Primary(copy, LvnTokens.RadiusSm);
            copy.clicked += () =>
            {
                GUIUtility.systemCopyBuffer = id;
                LvnMotion.FlashText(copy, LvnWords.Of("common.copied", "Copied"));
            };
            footer.Add(copy);

            return footer;
        }

        private static string Shorten(string id)
            => Lvn.LvnClip.Id(id);
    }
}
