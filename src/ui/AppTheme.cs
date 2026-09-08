using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WgSharp.Ui
{
    /// <summary>
    /// The app's color palette, light or dark depending on <see cref="IsDark"/>.
    /// Ported from LenovoRepoBuilder's Theme.cs (see that project's CLAUDE.md
    /// "Visual overhaul" section for the native-control history behind this
    /// pattern) — every member here is a live-read property, not a cached
    /// value, so a runtime toggle just needs Invalidate(), not per-control
    /// recoloring. IsDark defaults to the real OS setting and can be
    /// overridden at runtime (see SettingsPanel's "Dark theme" option); the
    /// override is persisted via AppSettings.ThemeIsDark/ThemeOverrideSet.
    /// </summary>
    internal static class AppTheme
    {
        public static bool IsDark;

        static AppTheme()
        {
            IsDark = DetectOsDarkMode();
        }

        public static bool DetectOsDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    object v = key.GetValue("AppsUseLightTheme");
                    return v != null && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // ---- fixed brand colors (same in both themes) ----
        public static Color Accent      { get { return Color.FromArgb(0x00, 0x78, 0xD4); } }
        public static Color AccentHover { get { return Color.FromArgb(0x10, 0x6E, 0xBE); } }
        public static Color AccentFg    { get { return Color.White; } }

        // ---- surfaces ----
        public static Color WindowBg { get { return IsDark ? Color.FromArgb(0x1A, 0x1A, 0x2E) : Color.White; } }
        public static Color PanelBg  { get { return IsDark ? Color.FromArgb(0x25, 0x25, 0x40) : Color.White; } }
        public static Color Surface  { get { return IsDark ? Color.FromArgb(0x25, 0x25, 0x40) : Color.White; } }
        public static Color Border   { get { return IsDark ? Color.FromArgb(0x35, 0x35, 0x60) : Color.FromArgb(0xD0, 0xD0, 0xE0); } }

        // ---- text ----
        public static Color GroupText  { get { return IsDark ? Color.FromArgb(0xE8, 0xE8, 0xF0) : Color.FromArgb(0x20, 0x20, 0x20); } }
        public static Color FieldLabel { get { return IsDark ? Color.FromArgb(0x88, 0x88, 0xAA) : Color.FromArgb(0x55, 0x55, 0x55); } }
        public static Color FieldValue { get { return IsDark ? Color.FromArgb(0xE8, 0xE8, 0xF0) : Color.FromArgb(0x20, 0x20, 0x20); } }

        // ---- lists ----
        public static Color ListBg       { get { return IsDark ? Color.FromArgb(0x2E, 0x2E, 0x4E) : Color.White; } }
        public static Color ListSelBg    { get { return IsDark ? Color.FromArgb(0x3A, 0x3A, 0x5C) : SystemColors.Highlight; } }
        public static Color ListSelText  { get { return IsDark ? Color.FromArgb(0xE8, 0xE8, 0xF0) : Color.White; } }

        // ---- text entry (TextBox/RichTextBox fill) ----
        public static Color EntryBg { get { return IsDark ? Color.FromArgb(0x2E, 0x2E, 0x4E) : Color.White; } }
        public static Color EntryFg { get { return IsDark ? Color.FromArgb(0xE8, 0xE8, 0xF0) : Color.FromArgb(0x20, 0x20, 0x20); } }

        // ---- log ----
        public static Color LogBg { get { return IsDark ? Color.FromArgb(0x12, 0x12, 0x1E) : Color.White; } }
        public static Color LogFg { get { return IsDark ? Color.FromArgb(0xAA, 0xAA, 0xCC) : Color.FromArgb(0x20, 0x20, 0x20); } }

        // ---- status (used by ThemedMessageBox's No/Cancel buttons) ----
        public static Color Err  { get { return IsDark ? Color.FromArgb(0xE0, 0x50, 0x50) : Color.FromArgb(0xC4, 0x00, 0x00); } }
        public static Color Warn { get { return IsDark ? Color.FromArgb(0xF0, 0xA0, 0x50) : Color.FromArgb(0xB8, 0x5C, 0x00); } }

        // ---- AreaChart plot area ----
        public static Color PlotBg     { get { return IsDark ? Color.FromArgb(0x25, 0x25, 0x40) : Color.White; } }
        public static Color PlotBorder { get { return IsDark ? Color.FromArgb(0x35, 0x35, 0x60) : Color.FromArgb(0xCF, 0xD4, 0xDA); } }
        public static Color PlotGrid   { get { return IsDark ? Color.FromArgb(0x2A, 0x2A, 0x48) : Color.FromArgb(0xEC, 0xEF, 0xF2); } }

        // ---- EditConfigDialog syntax highlighting ----
        public static Color SyntaxSection { get { return IsDark ? Color.FromArgb(0x56, 0x9C, 0xD6) : Color.FromArgb(0x00, 0x4A, 0xCC); } }
        public static Color SyntaxKey     { get { return IsDark ? Color.FromArgb(0xC5, 0x86, 0xC0) : Color.FromArgb(0x7A, 0x3E, 0x9D); } }
        public static Color SyntaxValue   { get { return IsDark ? Color.FromArgb(0x9C, 0xDC, 0xFE) : Color.FromArgb(0x1F, 0x5F, 0xBF); } }
        public static Color SyntaxComment { get { return IsDark ? Color.FromArgb(0x6A, 0x99, 0x55) : Color.FromArgb(0x3C, 0x8A, 0x3C); } }
        public static Color SyntaxDefault { get { return IsDark ? Color.FromArgb(0xD4, 0xD4, 0xD4) : Color.FromArgb(0x20, 0x20, 0x20); } }
    }

    /// <summary>
    /// Theme-aware helpers applied to existing (already-constructed) controls —
    /// WgSharp builds its controls once in Designer.cs/constructors rather
    /// than per-tab like LenovoRepoBuilder, so these are appliers, not a
    /// factory. Ported/adapted from that project's Ctrl class.
    /// </summary>
    internal static class Ctrl
    {
        // Native (non-Flat) Button ignores BackColor under visual styles.
        // Called once at construction and again from MainForm.ApplyTheme()
        // on every toggle.
        public static void FlattenButton(Button b, bool accent)
        {
            b.UseVisualStyleBackColor = false;
            b.FlatStyle = FlatStyle.Flat;
            b.Cursor = Cursors.Hand;
            if (accent)
            {
                b.BackColor = AppTheme.Accent;
                b.ForeColor = AppTheme.AccentFg;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = AppTheme.AccentHover;
            }
            else
            {
                b.BackColor = AppTheme.Surface;
                b.ForeColor = AppTheme.FieldValue;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = AppTheme.Border;
                b.FlatAppearance.MouseOverBackColor = AppTheme.ListSelBg;
            }
        }

        public static void ThemeEntry(TextBox tb)
        {
            tb.BackColor = AppTheme.EntryBg;
            tb.ForeColor = AppTheme.EntryFg;
        }

        // Wraps a TextBox/ListBox/RichTextBox in a Panel whose own BackColor
        // draws a themed 1px border — BorderStyle.FixedSingle always renders
        // a fixed native color regardless of theme, and neither control
        // exposes that border's color as a settable property. Caller owns
        // the wrapper's Dock/Location/Size; this only owns the inner
        // control's BorderStyle/Dock and the wrapper's BackColor/Padding.
        public static Panel Bordered(Control inner)
        {
            TextBox tb = inner as TextBox;
            if (tb != null) tb.BorderStyle = BorderStyle.None;

            Panel p = new Panel();
            inner.Dock = DockStyle.Fill;
            p.Controls.Add(inner);
            p.BackColor = AppTheme.Border;
            p.Padding = new Padding(1);
            return p;
        }
    }

    // ContextMenuStrip/MenuStrip/ToolStrip always render with fixed native
    // (light) colors regardless of the owning Form's theme unless given an
    // explicit Renderer. Ported as-is from LenovoRepoBuilder's Theme.cs —
    // every color reads AppTheme.* live, so one shared instance assigned
    // once at construction stays correct across a runtime toggle.
    internal class ThemeColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return AppTheme.Surface; } }
        public override Color ImageMarginGradientBegin     { get { return AppTheme.Surface; } }
        public override Color ImageMarginGradientMiddle    { get { return AppTheme.Surface; } }
        public override Color ImageMarginGradientEnd       { get { return AppTheme.Surface; } }
        public override Color MenuBorder                   { get { return AppTheme.Border; } }
        public override Color MenuItemBorder                { get { return AppTheme.Accent; } }
        public override Color MenuItemSelected               { get { return AppTheme.ListSelBg; } }
        public override Color MenuItemSelectedGradientBegin  { get { return AppTheme.ListSelBg; } }
        public override Color MenuItemSelectedGradientEnd    { get { return AppTheme.ListSelBg; } }
        public override Color MenuItemPressedGradientBegin   { get { return AppTheme.ListSelBg; } }
        public override Color MenuItemPressedGradientEnd     { get { return AppTheme.ListSelBg; } }
        public override Color SeparatorDark                  { get { return AppTheme.Border; } }
        public override Color SeparatorLight                 { get { return AppTheme.Border; } }
    }

    internal class ThemeMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemeMenuRenderer() : base(new ThemeColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = AppTheme.FieldValue;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(AppTheme.Surface))
                e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
    }
}
