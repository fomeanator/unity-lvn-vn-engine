using Lvn.Content;
using Lvn.UI.Screens;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Builds a <see cref="VnTheme"/> for the in-game dialogue box and choices
    /// from the manifest's <see cref="LvnUiConfig"/>. Pure data mapping: every
    /// field is optional and falls back to the corresponding <see cref="VnTheme"/>
    /// default, so a host can ship an empty <c>ui</c> block and still get the
    /// reference look, then override colour/size/spacing piece by piece from the
    /// manifest — the same configurability the shell screens already have.
    /// </summary>
    public static class VnThemeBuilder
    {
        /// <summary>Returns a theme with any present config fields applied over the
        /// engine defaults. A null config (or null sections) yields plain defaults.</summary>
        public static VnTheme From(LvnUiConfig ui, VnTheme baseline = null)
        {
            var t = baseline ?? new VnTheme();
            if (ui == null) return t;

            var d = ui.dialogue;
            if (d != null)
            {
                t.PanelColor = UiColor.Parse(d.panel_color, t.PanelColor);
                t.TextColor = UiColor.Parse(d.text_color, t.TextColor);
                t.SpeakerColor = UiColor.Parse(d.speaker_color, t.SpeakerColor);

                if (d.body_size.HasValue) t.BodyFontSize = Mathf.RoundToInt(d.body_size.Value);
                if (d.speaker_size.HasValue) t.SpeakerFontSize = Mathf.RoundToInt(d.speaker_size.Value);
                if (d.corner_radius.HasValue) t.PanelCornerRadius = d.corner_radius.Value;

                if (!string.IsNullOrEmpty(d.align)) t.BoxAlign = d.align;
                if (d.max_width_percent.HasValue) t.BoxMaxWidthPercent = d.max_width_percent.Value;
                if (d.width_percent.HasValue) t.BoxWidthPercent = d.width_percent.Value;
                if (d.max_height_percent.HasValue) t.BoxMaxHeightPercent = d.max_height_percent.Value;
                if (d.x_percent.HasValue) t.BoxXPercent = d.x_percent.Value;
                if (d.y_percent.HasValue) t.BoxYPercent = d.y_percent.Value;
                if (!string.IsNullOrEmpty(d.anchor)) t.BoxAnchor = d.anchor;
                if (d.edge_padding.HasValue) t.EdgePadding = d.edge_padding.Value;
                if (d.bottom_padding.HasValue) t.BottomPadding = d.bottom_padding.Value;
                if (d.panel_padding_x.HasValue) t.PanelPaddingX = d.panel_padding_x.Value;
                if (d.panel_padding_y.HasValue) t.PanelPaddingY = d.panel_padding_y.Value;
                if (d.panel_min_height.HasValue) t.PanelMinHeight = d.panel_min_height.Value;
                if (d.name_padding_x.HasValue) t.NamePaddingX = d.name_padding_x.Value;
                if (d.name_padding_y.HasValue) t.NamePaddingY = d.name_padding_y.Value;

                if (d.chars_per_second.HasValue) t.CharsPerSecond = d.chars_per_second.Value;
                if (d.fade_width.HasValue) t.FadeWidth = d.fade_width.Value;

                if (!string.IsNullOrEmpty(d.font)) t.FontResourcePath = d.font;
                if (d.nvl.HasValue) t.Nvl = d.nvl.Value;
                if (d.nvl_top.HasValue) t.NvlTop = d.nvl_top.Value;

                // Background-image urls (VnStage resolves them to sprites); slice px.
                if (!string.IsNullOrEmpty(d.panel_image)) t.PanelImageUrl = d.panel_image;
                if (!string.IsNullOrEmpty(d.name_image)) t.PlateImageUrl = d.name_image;
                if (d.panel_slice.HasValue) t.PanelSlice = d.panel_slice.Value;
            }

            var c = ui.choices;
            if (c != null)
            {
                t.ChoiceColor = UiColor.Parse(c.color, t.ChoiceColor);
                t.ChoiceHoverColor = UiColor.Parse(c.hover_color, t.ChoiceHoverColor);
                t.ChoiceTextColor = UiColor.Parse(c.text_color, t.ChoiceTextColor);
                t.ChoiceCostColor = UiColor.Parse(c.cost_color, t.ChoiceCostColor);

                if (!string.IsNullOrEmpty(c.align)) t.ChoiceAlign = c.align;
                if (!string.IsNullOrEmpty(c.valign)) t.ChoiceVAlign = c.valign;
                if (c.y_percent.HasValue) t.ChoiceYPercent = c.y_percent.Value;

                if (c.font_size.HasValue) t.ChoiceFontSize = Mathf.RoundToInt(c.font_size.Value);
                if (c.min_width_percent.HasValue) t.ChoiceMinWidthPercent = c.min_width_percent.Value;
                if (c.max_width_percent.HasValue) t.ChoiceMaxWidthPercent = c.max_width_percent.Value;
                if (c.spacing.HasValue) t.ChoiceSpacing = c.spacing.Value;
                if (c.padding_x.HasValue) t.ChoicePaddingX = c.padding_x.Value;
                if (c.padding_y.HasValue) t.ChoicePaddingY = c.padding_y.Value;
                if (c.corner_radius.HasValue) t.ChoiceCornerRadius = c.corner_radius.Value;

                if (!string.IsNullOrEmpty(c.button_image)) t.ChoiceImageUrl = c.button_image;
                if (!string.IsNullOrEmpty(c.button_hover_image)) t.ChoiceHoverImageUrl = c.button_hover_image;
                if (c.button_slice.HasValue) t.ChoiceSlice = c.button_slice.Value;
            }

            var m = ui.menu;
            if (m != null)
            {
                t.MenuBgColor = UiColor.Parse(m.bg_color, t.MenuBgColor);
                t.MenuTextColor = UiColor.Parse(m.text_color, t.MenuTextColor);
                t.MenuDimTextColor = UiColor.Parse(m.dim_text_color, t.MenuDimTextColor);
                t.MenuFabColor = UiColor.Parse(m.fab_color, t.MenuFabColor);
                t.MenuScrimColor = UiColor.Parse(m.scrim_color, t.MenuScrimColor);
                if (m.corner_radius.HasValue) t.MenuCornerRadius = m.corner_radius.Value;
                if (m.show_rollback.HasValue) t.MenuShowRollback = m.show_rollback.Value;
                if (m.show_menu.HasValue) t.MenuShowMenu = m.show_menu.Value;
                if (m.labels != null && m.labels.Count > 0) t.MenuLabels = m.labels;
            }

            return t;
        }
    }
}
