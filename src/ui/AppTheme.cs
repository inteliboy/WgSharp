using System.Drawing;

namespace WgSharp.Ui
{
    /// <summary>
    /// The app's fixed light color palette. WgSharp is light-theme-only — there
    /// used to be a dark mode here, but the custom-painted buttons and the
    /// overlay tab strip it required were more trouble than they were worth
    /// (fighting native control rendering), so it was removed entirely. These
    /// are just the colors the UI uses, not a theme switch.
    /// </summary>
    internal static class AppTheme
    {
        public static readonly Color WindowBg = Color.White;
        public static readonly Color PanelBg = Color.White;
        public static readonly Color GroupText = Color.FromArgb(0x20, 0x20, 0x20);
        public static readonly Color FieldLabel = Color.FromArgb(0x55, 0x55, 0x55);
        public static readonly Color FieldValue = Color.FromArgb(0x20, 0x20, 0x20);
        public static readonly Color ListBg = Color.White;
        public static readonly Color ListSelBg = SystemColors.Highlight;
        public static readonly Color ListSelText = Color.White;
        public static readonly Color LogBg = Color.White;
    }
}
