namespace Lvn.Content
{
    /// <summary>
    /// Manifest-driven theme for the engine's built-in novel screens — the
    /// loading screen, the chapter title card, and the name-input screen. Every
    /// field is optional; the components fall back to sensible defaults when a
    /// value is absent, so a host can ship an empty <c>ui</c> block and still get
    /// a working set of screens, then override piece by piece from the manifest.
    ///
    /// <para>Colors are hex strings (<c>"#rrggbb"</c> or <c>"#rrggbbaa"</c>);
    /// image fields are content urls resolved through <c>ILvnAssets</c>; layout
    /// numbers are screen fractions (0..1) or pixels as noted. No UnityEngine
    /// types here, so the whole theme is plain serializable data.</para>
    /// </summary>
    public sealed class LvnUiConfig
    {
        public LoadingScreenConfig loading;
        public TitleCardConfig title;
        public NameInputConfig name_input;
        public BootScreenConfig boot;
        public CarouselConfig carousel;
        public HudConfig hud;
        public DialogueConfig dialogue;
        public ChoicesConfig choices;
        public MenuConfig menu;
    }

    /// <summary>The in-game quick menu (StageMenu): floating buttons, the sheet,
    /// save/load slots, history and settings panels. Every field optional — the
    /// engine's neutral dark look is the default.</summary>
    public sealed class MenuConfig
    {
        public string bg_color;       // sheet/panel fill; default #14141af7
        public string text_color;     // items and labels; default #f2eee1
        public string dim_text_color; // secondary text (previews, narration); default #ccc7bd
        public string fab_color;      // floating-button fill; default #00000059
        public string scrim_color;    // fullscreen backdrop; default #0000008c
        public float? corner_radius;  // sheet/panel rounding; default 12
        public bool? show_rollback;   // the ↩ button; default true
        public bool? show_menu;       // the ☰ button; default true
    }

    /// <summary>In-game dialogue box: colours, fonts, padding and the typewriter
    /// reveal. Maps onto the engine's <c>VnTheme</c> so the whole game — not just
    /// the shell screens — is themeable from the manifest. Every field optional.</summary>
    public sealed class DialogueConfig
    {
        public string panel_color;       // box + nameplate fill; default #0d0d14cc
        public string text_color;        // body text; default #f5f5f5
        public string speaker_color;     // name text; default #ffd166

        public float? body_size;         // px; default 34
        public float? speaker_size;      // px; default 24
        public float? corner_radius;     // px; default 12

        public string align;             // box placement: "stretch" (default bottom bar) | "center" | "left" | "right"; non-stretch hugs the text
        public float? max_width_percent; // content-hug cap when align != stretch; default 80
        public float? width_percent;     // fixed box width % (>0 overrides the content-hug); default hug
        public float? max_height_percent;// cap box height as a screen %; default unbounded

        // Free popup: set x/y (a screen % 0..100) to float the box anywhere on
        // screen instead of docking it to the bottom — the universal popup mode.
        public float? x_percent;         // horizontal position 0=left … 100=right
        public float? y_percent;         // vertical position 0=top … 100=bottom
        public string anchor;            // which box point lands on (x,y): "center" (default), "bottom-center", "top-left", …

        public float? edge_padding;      // inset from screen edges; default 24
        public float? bottom_padding;    // gap to screen bottom; default 28
        public float? panel_padding_x;   // body inner padding; default 22
        public float? panel_padding_y;   // default 18
        public float? panel_min_height;  // default 128
        public float? name_padding_x;    // nameplate inner padding; default 14
        public float? name_padding_y;    // default 4

        public float? chars_per_second;  // typewriter speed; default 45
        public float? fade_width;        // soft per-glyph fade, trailing chars; default 5

        public string font;              // Resources path to a Font (e.g. "Fonts/Serif")
        public bool? nvl;                // NVL mode: tall full-screen text panel; default false
        public float? nvl_top;           // NVL top inset, screen fraction; default 0.12

        public string panel_image;       // content url: body-panel background sprite (overrides panel_color)
        public string name_image;        // content url: nameplate background sprite
        public int? panel_slice;         // 9-slice border px for the panel/name sprites; default 0 (stretch)
    }

    /// <summary>In-game choice buttons: colours, font, width and spacing.</summary>
    public sealed class ChoicesConfig
    {
        public string color;             // button fill; default #1f1f29eb
        public string hover_color;       // default #33333eF5
        public string text_color;        // default #f5f5f5
        public string cost_color;        // cost/lock label; default #e6a33b

        public string align;             // horizontal placement: "center" (default) | "left" | "right"
        public string valign;            // vertical placement: "center" (default) | "top" | "bottom"
        public float? y_percent;         // free vertical: top of the stack at this screen % (overrides valign)

        public float? font_size;         // px; default 28
        public float? min_width_percent; // default 58
        public float? max_width_percent; // default 86
        public float? spacing;           // gap between buttons; default 10
        public float? padding_x;         // button inner padding; default 20
        public float? padding_y;         // default 12
        public float? corner_radius;     // default 10

        public string button_image;       // content url: button background sprite (overrides color)
        public string button_hover_image; // content url: hovered-button sprite (defaults to button_image)
        public int? button_slice;         // 9-slice border px for button sprites; default 0 (stretch)
    }

    /// <summary>The app boot / preload splash shown at launch (logo + progress).</summary>
    public sealed class BootScreenConfig
    {
        public string bg_color;          // default #0a0a0e
        public string bg_url;            // optional splash backdrop
        public string logo_url;          // optional centred logo
        public float? logo_width;        // screen fraction; default 0.5
        public float? logo_y;            // logo centre y; default 0.4

        public string bar_track_color;   // default #ffffff22
        public string bar_fill_color;    // default #c8a050
        public string bar_fill_url;      // optional fill art
        public float? bar_y;             // default 0.86
        public float? bar_width;         // default 0.6
        public float? bar_height;        // default 0.014
        public bool? show_percent;       // default true
        public string percent_color;     // default #cfc8bd
        public float? min_seconds;       // default 1.0
    }

    /// <summary>The title slider / carousel on the main menu.</summary>
    public sealed class CarouselConfig
    {
        public string bg_color;          // default #101015
        public float? card_width;        // screen fraction; default 0.62
        public float? card_height;       // screen fraction; default 0.62
        public float? card_gap;          // screen fraction; default 0.06
        public string card_bg_color;     // default #1c1c22 (behind a missing cover)
        public float? card_radius;       // px; default 18

        public string title_color;       // default #f4ecd8
        public float? title_size;        // px; default 40
        public string subtitle_color;    // default #cbb98f
        public float? subtitle_size;     // px; default 22

        public string play_text;         // default "Play"
        public string continue_text;     // Play label when there's progress; default "Continue"
        public string chapters_text;     // the chapter-picker button; default "Chapters"
        public string play_color;        // default #f4ecd8
        public string play_bg_color;     // default #3a3a44
        public string dot_color;         // page-dot inactive; default #ffffff55
        public string dot_active_color;  // default #f4ecd8
    }

    /// <summary>The in-game top HUD: chapter progress + currency pills.</summary>
    public sealed class HudConfig
    {
        public string bg_color;          // strip background; default #00000088
        public float? height;            // screen fraction; default 0.07
        public string progress_icon_url; // optional icon left of the percent
        public string progress_color;    // default #f4ecd8
        public bool? show_progress;      // default true

        public string pill_bg_color;     // default #00000066
        public string pill_text_color;   // default #f4ecd8
        public string default_currency_icon_url; // fallback pill icon
    }

    /// <summary>Look and behaviour of the loading screen (background, scrim, and a
    /// progress bar with optional track/fill/frame art).</summary>
    public sealed class LoadingScreenConfig
    {
        public string bg_color;          // backdrop behind everything; default #000000
        public string bg_url;            // optional static backdrop image
        public string fog_url;           // optional atmospheric overlay (fades in late)

        public string scrim_color;       // dark wash over the bg; default #000000
        public float? scrim_opacity;     // default 0.65

        public string bar_track_url;     // bar background art (optional)
        public string bar_fill_url;      // bar fill art (optional)
        public string bar_frame_url;     // bar frame overlay art (optional)
        public string bar_fill_color;    // solid fill when no fill art; default #c8a050
        public string bar_track_color;   // solid track when no track art; default #ffffff22

        public float? bar_x;             // bar centre x, screen fraction; default 0.5
        public float? bar_y;             // bar centre y, screen fraction; default 0.82
        public float? bar_width;         // screen fraction; default 0.7
        public float? bar_height;        // screen fraction; default 0.018
        public float? fill_span_percent; // fill width at 100%; default 100 (use 90 for sprite caps)

        public bool? show_percent;       // default true
        public bool? show_file;          // default true
        public bool? show_hint;          // default true
        public string percent_color;     // default #ffffff
        public string hint_color;        // default #cfc8bd
        public string file_color;        // default #9a948a

        public string[] tips;            // rotating hint lines during loading
        public float? min_seconds;       // hold the screen at least this long; default 0
    }

    /// <summary>The "Chapter N / chapter name" reveal card.</summary>
    public sealed class TitleCardConfig
    {
        public string frame_url;         // optional decorative frame behind the text
        public string fog_url;           // optional fog that fades in with the card

        public string chapter_color;     // default #f4ecd8
        public string subtitle_color;    // default #cbb98f
        public float? chapter_size;      // px; default 64
        public float? subtitle_size;     // px; default 34

        public float? hold_seconds;      // how long the card stays; default 2.5
        public float? fade_seconds;      // fade-in duration; default 0.6
    }

    /// <summary>The character name-input screen.</summary>
    public sealed class NameInputConfig
    {
        public string bg_url;            // full-screen backdrop
        public string hero_url;          // optional character art
        public string field_url;         // optional text-field background art
        public string button_url;        // optional confirm-button art
        public string badge_url;         // optional speaker-name badge art

        public string bg_color;          // default #101015
        public string prompt;            // default "Enter your name"
        public string speaker_label;     // default "Name"
        public string default_name;      // pre-filled value; default ""
        public string confirm_text;      // default "Confirm"
        public int? max_length;          // default 24

        public string prompt_color;      // default #cbb98f
        public string text_color;        // default #f4ecd8
        public string field_color;       // default #1c1c22 (used when no field art)
        public string button_color;      // default #3a3a44 (used when no button art)
    }
}
