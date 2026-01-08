using System;
using System.Runtime.InteropServices;

namespace CharaAnime
{
    /// <summary>
    /// 轻量级的 Windows 原生文件选择器封装
    /// 直接调用 comdlg32.dll，避免依赖 System.Windows.Forms
    /// </summary>
    public static class SimpleFileBrowser
    {
        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        /// <summary>
        /// 打开文件选择对话框。
        /// filter 采用 WinAPI 格式：
        /// 例如："VMD File\0*.vmd\0All Files\0*.*\0\0"
        /// </summary>
        public static string OpenFile(string title, string filter, string initialDir = "")
        {
            OpenFileName ofn = new OpenFileName();

            ofn.lStructSize = Marshal.SizeOf(ofn);
            ofn.lpstrFilter = filter;
            ofn.lpstrFile = new string(new char[256]);
            ofn.nMaxFile = ofn.lpstrFile.Length;
            ofn.lpstrFileTitle = new string(new char[64]);
            ofn.nMaxFileTitle = ofn.lpstrFileTitle.Length;
            ofn.lpstrTitle = title;

            if (!string.IsNullOrEmpty(initialDir))
            {
                ofn.lpstrInitialDir = initialDir;
            }

            // 标志位：OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            // 0x00000008 | 0x00000800 | 0x00001000
            ofn.Flags = 0x00000008 | 0x00000800 | 0x00001000;

            if (GetOpenFileName(ofn))
            {
                return ofn.lpstrFile;
            }
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class OpenFileName
    {
        public int lStructSize = 0;
        public IntPtr hwndOwner = IntPtr.Zero;
        public IntPtr hInstance = IntPtr.Zero;
        public string lpstrFilter = null;
        public string lpstrCustomFilter = null;
        public int nMaxCustFilter = 0;
        public int nFilterIndex = 0;
        public string lpstrFile = null;
        public int nMaxFile = 0;
        public string lpstrFileTitle = null;
        public int nMaxFileTitle = 0;
        public string lpstrInitialDir = null;
        public string lpstrTitle = null;
        public int Flags = 0;
        public short nFileOffset = 0;
        public short nFileExtension = 0;
        public string lpstrDefExt = null;
        public IntPtr lCustData = IntPtr.Zero;
        public IntPtr lpfnHook = IntPtr.Zero;
        public string lpTemplateName = null;
        public IntPtr pvReserved = IntPtr.Zero;
        public int dwReserved = 0;
        public int FlagsEx = 0;
    }
}


