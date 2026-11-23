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

            // GTA VI: CodeWalker.RPFExplorer.exe <folder with RPF9 archives> [--exe=<game_scarlett_master.exe>] [--names=<rpf9_names.tsv>]
            foreach (var a in args)
            {
                if (a.StartsWith("--exe=")) GTAFolder.Gta6Exe = a.Substring(6).Trim('"');
                else if (a.StartsWith("--names=")) GTAFolder.Gta6Names = a.Substring(8).Trim('"');
                else if (!a.StartsWith("--") && !GTAFolder.UseGta6Folder(a.Trim('"')))
                {
                    MessageBox.Show("Not a GTA VI folder (no RPF9 archives in it): " + a, "CodeWalker RPF Explorer");
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ExploreForm());

            GTAFolder.UpdateSettings();
        }
    }
}
