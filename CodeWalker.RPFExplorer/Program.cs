using CodeWalker.GameFiles;
using CodeWalker.Properties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodeWalker.RPFExplorer
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            //Process.Start("CodeWalker.exe", "explorer");

            // Open a game folder for this session only (the saved folder is unchanged):
            //   GTA VI: "CodeWalker RPF Explorer.exe" <folder with RPF9 archives> [--exe=<game_scarlett_master.exe>] [--names=<extra hash TAB name list>]
            //   GTA V:  "CodeWalker RPF Explorer.exe" <folder with GTA5.exe / gta5_enhanced.exe> [--legacy | --enhanced]
            bool? gen9 = null;
            if (args.Contains("--legacy")) gen9 = false;
            if (args.Contains("--enhanced")) gen9 = true;
            foreach (var a in args)
            {
                if (a.StartsWith("--exe=")) GTAFolder.Gta6Exe = a.Substring(6).Trim('"');
                else if (a.StartsWith("--names=")) GTAFolder.Gta6Names = a.Substring(8).Trim('"');
                else if (!a.StartsWith("--") && !GTAFolder.UseGta6Folder(a.Trim('"')) && !GTAFolder.UseGta5Folder(a.Trim('"'), gen9))
                {
                    MessageBox.Show("Not a game folder (no RPF9 archives, " + (gen9 == true ? "no gta5_enhanced.exe" : "no GTA5.exe") + "): " + a, "CodeWalker RPF Explorer");
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new ExploreForm();
            form.FormClosed += (s, e) =>
            {
                // ExploreForm has saved its settings by now (its own FormClosed handler runs first). Tearing the window
                // down would delete the folder tree node by node (one notification per node, seconds of lag for the
                // GTA VI tree and system-wide accessibility events), so end the process here instead.
                GTAFolder.UpdateSettings();
                form.Hide();
                Environment.Exit(0);
            };
            Application.Run(form);

            GTAFolder.UpdateSettings();
        }
    }
}
