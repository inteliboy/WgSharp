using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace WgSharp.Ui
{
    /// <summary>
    /// Pulls the largest icon frame out of the running exe's own embedded icon
    /// resource (the one baked in via build.cmd's /win32icon) and decodes it
    /// directly as PNG, for places that want a crisp, large rendering — the
    /// About dialog's icon, specifically.
    ///
    /// Why not just use System.Drawing.Icon: Icon.ExtractAssociatedIcon only
    /// ever returns one small, fixed shell-association size (commonly 32x32),
    /// so stretching it up to display large looks soft. Worse, Icon's
    /// file/size-selection constructor (`new Icon(path, size)`) has a
    /// long-standing GDI+ limitation with PNG-COMPRESSED icon directory
    /// entries — which is what large modern icons use, including ours at
    /// 256x256 — where it can end up treating the raw PNG byte stream as if
    /// it were uncompressed pixel data. That produces exactly the "white
    /// noise with some color pixels" look: compressed bytes misread as a
    /// bitmap. Reading the resource's raw bytes ourselves and decoding them
    /// with .NET's actual PNG decoder (Image.FromStream) sidesteps that
    /// limitation entirely, and needs no separate .ico file on disk — the
    /// icon is already embedded in the exe being run.
    /// </summary>
    internal static class AppIconLoader
    {
        private const int RT_ICON = 3;
        private const int RT_GROUP_ICON = 14;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

        /// <summary>Returns the largest embedded icon frame as a Bitmap, or null on any failure.</summary>
        public static Bitmap LoadLargestEmbeddedIcon()
        {
            try
            {
                IntPtr hModule = GetModuleHandle(null);
                if (hModule == IntPtr.Zero) return null;

                IntPtr groupName = IntPtr.Zero;
                EnumResNameProc callback = delegate (IntPtr hMod, IntPtr type, IntPtr name, IntPtr param)
                {
                    groupName = name;
                    return false; // one RT_GROUP_ICON is all /win32icon ever embeds; stop at the first
                };
                EnumResourceNames(hModule, (IntPtr)RT_GROUP_ICON, callback, IntPtr.Zero);
                if (groupName == IntPtr.Zero) return null;

                IntPtr hGroupResInfo = FindResource(hModule, groupName, (IntPtr)RT_GROUP_ICON);
                if (hGroupResInfo == IntPtr.Zero) return null;
                IntPtr hGroupRes = LoadResource(hModule, hGroupResInfo);
                IntPtr pGroup = LockResource(hGroupRes);
                if (pGroup == IntPtr.Zero) return null;

                // NEWHEADER: WORD reserved, WORD type, WORD count, then count
                // GRPICONDIRENTRY records of 14 bytes each:
                //   BYTE width, BYTE height, BYTE colorCount, BYTE reserved,
                //   WORD planes, WORD bitCount, DWORD bytesInRes, WORD id.
                short count = Marshal.ReadInt16(pGroup, 4);
                int bestId = -1;
                uint bestBytes = 0;
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = (IntPtr)(pGroup.ToInt64() + 6 + i * 14);
                    uint bytesInRes = (uint)Marshal.ReadInt32(entry, 8);
                    int id = Marshal.ReadInt16(entry, 12) & 0xFFFF;
                    // Pick the largest by encoded size — for a PNG-compressed
                    // icon set, the highest-resolution frame is reliably the
                    // largest byte count, with no need to special-case the
                    // ICO format's "0 means 256" width/height convention.
                    if (bytesInRes > bestBytes) { bestBytes = bytesInRes; bestId = id; }
                }
                if (bestId < 0) return null;

                IntPtr hIconResInfo = FindResource(hModule, (IntPtr)bestId, (IntPtr)RT_ICON);
                if (hIconResInfo == IntPtr.Zero) return null;
                IntPtr hIconRes = LoadResource(hModule, hIconResInfo);
                IntPtr pIcon = LockResource(hIconRes);
                uint size = SizeofResource(hModule, hIconResInfo);
                if (pIcon == IntPtr.Zero || size == 0) return null;

                byte[] buffer = new byte[size];
                Marshal.Copy(pIcon, buffer, 0, (int)size);

                // For our icon, every frame is PNG-compressed (see the icon
                // build script), so this is a plain PNG byte stream — decode
                // it with the real PNG decoder instead of anything ICO-aware.
                using (var ms = new MemoryStream(buffer))
                using (Image img = Image.FromStream(ms))
                    return new Bitmap(img); // clone so it outlives the MemoryStream
            }
            catch { return null; }
        }
    }
}
