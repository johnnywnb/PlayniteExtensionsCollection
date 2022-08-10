﻿using PlayState.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace PlayState
{
    public static class ProcessesHandler
    {
        private const string wmiQueryString = "SELECT ProcessId, ExecutablePath FROM Win32_Process";
        private const UInt32 WM_CLOSE = 0x0010;
        [DllImport("ntdll.dll", PreserveSig = false)]
        public static extern void NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll", PreserveSig = false)]
        public static extern void NtResumeProcess(IntPtr processHandle);
        public delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private static readonly List<string> exclusionList = new List<string>
        {
            "7z.exe",
            "7za.exe",
            "archive.exe",
            "asset_.exe",
            "anetdrop.exe",
            "bat_to_exe_convertor.exe",
            "bssndrpt.exe",
            "bootboost.exe",
            "bootstrap.exe",
            "cabarc.exe",
            "cdkey.exe",
            "cheat engine.exe",
            "cheatengine",
            "civ2map.exe",
            "config",
            "closepw.exe",
            "crashdump",
            "crashreport",
            "crc32.exe",
            "creationkit.exe",
            "creatureupload.exe",
            "easyhook.exe",
            "dgvoodoocpl.exe",
            "dotnet",
            "doc.exe",
            "dxsetup",
            "dw.exe",
            "enbinjector.exe",
            "havokbehaviorpostprocess.exe",
            "help",
            "install",
            "launch_game.exe",
            "langselect.exe",
            "language.exe",
            "launch",
            "loader",
            "mapcreator.exe",
            "master_dat_fix_up.exe",
            "md5sum.exe",
            "mgexegui.exe",
            "modman.exe",
            "modorganizer.exe",
            "notepad++.exe",
            "notification_helper.exe",
            "oalinst.exe",
            "palettestealersuspender.exe",
            "pak",
            "patch",
            "planet_mapgen.exe",
            "papyrus",
            "radtools.exe",
            "readspr.exe",
            "register.exe",
            "sekirofpsunlocker",
            "settings",
            "setup",
            "scuex64.exe",
            "synchronicity.exe",
            "syscheck.exe",
            "systemsurvey.exe",
            "tes construction set.exe",
            "texmod.exe",
            "unins",
            "unitycrashhandler",
            "x360ce",
            "unpack",
            "unx_calibrate",
            "update",
            "unrealcefsubprocess.exe",
            "url.exe",
            "versioned_json.exe",
            "vcredist",
            "xtexconv.exe",
            "xwmaencode.exe",
            "website.exe",
            "wide_on.exe"
        };

        public static List<ProcessItem> GetProcessesWmiQuery(bool useExclusionList, string gameInstallDir, string exactPath = null)
        {
            using (var searcher = new ManagementObjectSearcher(wmiQueryString))
            using (var results = searcher.Get())
            {
                // Unfortunately due to Playnite being a 32 bits process, the GetProcess()
                // method can't access needed values of 64 bits processes, so it's needed
                // to correlate with data obtained from a WMI query that is exponentially slower.
                // It needs to be done this way until #1199 is done
                var query = from p in Process.GetProcesses()
                            join mo in results.Cast<ManagementObject>()
                            on p.Id equals (int)(uint)mo["ProcessId"]
                            select new ProcessItem(p, (string)mo["ExecutablePath"]);

                var gameProcesses = new List<ProcessItem>();
                if (!exactPath.IsNullOrEmpty())
                {
                    AddGameProcessesExactPath(exactPath, query, gameProcesses);
                }
                else
                {
                    AddGameProcessesThatStartWithPath(useExclusionList, gameInstallDir, query, gameProcesses);
                }

                return gameProcesses;
            }
        }

        private static void AddGameProcessesExactPath(string exactPath, IEnumerable<ProcessItem> query, List<ProcessItem> gameProcesses)
        {
            foreach (var queryItem in query)
            {
                if (queryItem.ExecutablePath.IsNullOrEmpty() ||
                    !queryItem.ExecutablePath.Equals(exactPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                gameProcesses.Add(queryItem);
            }
        }

        private static void AddGameProcessesThatStartWithPath(bool useExclusionList, string startPath, IEnumerable<ProcessItem> query, List<ProcessItem> gameProcesses)
        {
            foreach (var queryItem in query)
            {
                if (queryItem.ExecutablePath.IsNullOrEmpty() ||
                    !queryItem.ExecutablePath.StartsWith(startPath, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                if (useExclusionList &&
                    exclusionList.Any(e => Path.GetFileName(queryItem.ExecutablePath).Contains(e, StringComparison.InvariantCultureIgnoreCase)))
                {
                    continue;
                }

                gameProcesses.Add(queryItem);
            }
        }

        internal static void CloseProcessItem(List<ProcessItem> gameProcesses)
        {
            foreach (var processItem in gameProcesses)
            {
                try
                {
                    ExitProcessItem(processItem);
                }
                catch
                {

                }
            }
        }

        private static void ExitProcessItem(ProcessItem processItem)
        {
            if (processItem.Process == null)
            {
                return;
            }
            
            if (processItem.Process.MainWindowHandle == null || processItem.Process.MainWindowHandle ==  IntPtr.Zero)
            {
                // Try closing application by sending WM_CLOSE to all child windows in all threads.
                foreach (ProcessThread pt in processItem.Process.Threads)
                {
                    EnumThreadWindows((uint)pt.Id, new EnumThreadDelegate(EnumThreadCallback), IntPtr.Zero);
                }
            }
            else if (processItem.Process.CloseMainWindow()) // Try to close main window
            {
                // Free resources used by this Process object.
                processItem.Process.Close();
            }
            else
            {
                // Kill as a last resource :(
                processItem.Process.Kill();
            }
        }

        private static bool EnumThreadCallback(IntPtr hWnd, IntPtr lParam)
        {
            // Close the enumerated window.
            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            return true;
        }
    }
}