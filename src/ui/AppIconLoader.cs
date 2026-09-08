using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace WgSharp.Ui
{
    /// <summary>
    /// Reads icon frames out of the running exe's own embedded icon resource
    /// (the one baked in via build.cmd's /win32icon) for places that want
    /// better fidelity than System.Drawing.Icon's own file loading gives:
    /// LoadLargestEmbeddedIcon decodes just the biggest frame as a Bitmap (the
    /// About dialog's icon), LoadFullEmbeddedIcon rebuilds every frame into a
    /// proper multi-resolution Icon (the window/taskbar/tray icon).
    ///
    /// Why not just use System.Drawing.Icon directly: Icon.ExtractAssociatedIcon
    /// only ever returns one small, fixed shell-association frame (commonly
    /// 32x32) and discards the rest, so stretching it up to display large looks
    /// soft, and Windows has nothing else to pick from at higher DPI either.
    /// Icon's file/size-selection constructor (`new Icon(path, size)`) has a
    /// separate, long-standing GDI+ limitation with PNG-COMPRESSED icon
    /// directory entries — which is what large modern icons use, including
    /// ours at 256x256 — where it can end up treating the raw PNG byte stream
    /// as if it were uncompressed pixel data. That produces exactly the "white
    /// noise with some color pixels" look: compressed bytes misread as a
    /// bitmap. Reading the resource's raw bytes ourselves sidesteps both
    /// problems and needs no separate .ico file on disk — the icon is already
    /// embedded in the exe being run.
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

        /// <summary>
        /// Rebuilds a proper multi-resolution Icon from every frame embedded in
        /// the running exe's own icon resource (not just the largest), for the
        /// window/taskbar/tray icon.
        ///
        /// Why not Icon.ExtractAssociatedIcon: it only ever returns one small,
        /// fixed shell-association frame (commonly 32x32) and discards the rest,
        /// so Windows has nothing but that one frame to scale up for the taskbar,
        /// Alt+Tab, and title bar at higher DPI - it comes out visibly blurry/
        /// blocky there even though the exe's icon resource has a crisp 256x256
        /// frame sitting right next to it, unused.
        ///
        /// This reassembles a standalone in-memory .ico (ICONDIR + one
        /// ICONDIRENTRY per frame + each frame's own raw bytes, taken directly
        /// from the RT_GROUP_ICON directory and its RT_ICON entries) and loads
        /// it with Icon's stream constructor, which hands the whole multi-frame
        /// blob to the OS (CreateIconFromResourceEx) instead of pre-selecting a
        /// single frame itself - Windows then always picks the right resolution
        /// for wherever the icon is being drawn. This is a different code path
        /// than the file/size-selection constructor (`new Icon(path, size)`)
        /// that LoadLargestEmbeddedIcon's own doc comment warns about, which is
        /// why this one doesn't need a manual PNG decode step.
        /// </summary>
        public static Icon LoadFullEmbeddedIcon()
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

                short count = Marshal.ReadInt16(pGroup, 4);
                if (count <= 0) return null;

                var frameBytes = new List<byte[]>();
                var frameHeaders = new List<byte[]>(); // 12-byte ICONDIRENTRY prefix (everything but dwImageOffset)

                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = (IntPtr)(pGroup.ToInt64() + 6 + i * 14);
                    byte width = Marshal.ReadByte(entry, 0);
                    byte height = Marshal.ReadByte(entry, 1);
                    byte colorCount = Marshal.ReadByte(entry, 2);
                    byte reserved = Marshal.ReadByte(entry, 3);
                    short planes = Marshal.ReadInt16(entry, 4);
                    short bitCount = Marshal.ReadInt16(entry, 6);
                    int id = Marshal.ReadInt16(entry, 12) & 0xFFFF;

                    IntPtr hIconResInfo = FindResource(hModule, (IntPtr)id, (IntPtr)RT_ICON);
                    if (hIconResInfo == IntPtr.Zero) continue;
                    IntPtr hIconRes = LoadResource(hModule, hIconResInfo);
                    IntPtr pIcon = LockResource(hIconRes);
                    uint size = SizeofResource(hModule, hIconResInfo);
                    if (pIcon == IntPtr.Zero || size == 0) continue;

                    byte[] buffer = new byte[size];
                    Marshal.Copy(pIcon, buffer, 0, (int)size);
                    frameBytes.Add(buffer);

                    using (var hs = new MemoryStream(12))
                    using (var hw = new BinaryWriter(hs))
                    {
                        hw.Write(width); hw.Write(height); hw.Write(colorCount); hw.Write(reserved);
                        hw.Write(planes); hw.Write(bitCount); hw.Write(buffer.Length);
                        frameHeaders.Add(hs.ToArray());
                    }
                }
                if (frameBytes.Count == 0) return null;

                using (var outMs = new MemoryStream())
                {
                    // Not `using (var w = new BinaryWriter(outMs))`: BinaryWriter.Dispose()
                    // also disposes/closes the stream it wraps, which made every call to
                    // this method throw ObjectDisposedException on the Position/Icon lines
                    // below (silently swallowed by the catch further down) - the icon never
                    // loaded and every caller fell through to its own null-check fallback.
                    var w = new BinaryWriter(outMs);
                    w.Write((short)0);              // idReserved
                    w.Write((short)1);               // idType: 1 = icon
                    w.Write((short)frameBytes.Count); // idCount

                    int offset = 6 + frameBytes.Count * 16;
                    for (int i = 0; i < frameBytes.Count; i++)
                    {
                        w.Write(frameHeaders[i]);
                        w.Write(offset);              // dwImageOffset
                        offset += frameBytes[i].Length;
                    }
                    for (int i = 0; i < frameBytes.Count; i++)
                        w.Write(frameBytes[i]);
                    w.Flush();

                    outMs.Position = 0;
                    return new Icon(outMs);
                }
            }
            catch { return null; }
        }
    }
}
