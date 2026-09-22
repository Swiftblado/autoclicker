using System;
using System.Threading;
using System.Windows.Forms;

namespace AutoClicker
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            bool isFirstInstance;
            using (new Mutex(true, "AutoClicker.SingleInstance", out isFirstInstance))
            {
                if (!isFirstInstance)
                {
                    MessageBox.Show("Auto Clicker is already running.", "Auto Clicker",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
