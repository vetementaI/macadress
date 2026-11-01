using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MacRandomizer
{
    public static class Mac
    {
        public static string Normalize(string value)
        {
            return (value ?? "").Replace(":", "").Replace("-", "").ToUpperInvariant();
        }
        public static bool Valid(string value) { return Regex.IsMatch(Normalize(value), "^[0-9A-F]{12}$"); }
        public static string Format(string value)
        {
            string s = Normalize(value);
            return Valid(s) ? string.Join(":", Enumerable.Range(0, 6).Select(i => s.Substring(i * 2, 2))) : "Недоступен";
        }
        public static string Generate()
        {
            byte[] bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            bytes[0] = 0x02; // Local unicast; compatible with drivers requiring a 02 prefix.
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }

    public sealed class Setting
    {
        public bool Exists;
        public string Value;
        public Setting(bool exists, string value) { Exists = exists; Value = value ?? ""; }
    }

    public interface IDevice
    {
        bool Enabled { get; }
        Setting ReadSetting();
        void WriteSetting(Setting setting);
        Setting LoadBaseline();
        void SaveBaseline(Setting setting);
        void DeleteBaseline();
        void Restart();
        string ReadMac();
        string WaitMac(string expected);
    }

    // Transaction logic is independent of WMI and registry so failure recovery can be tested offline.
    public static class ChangeEngine
    {
        public static string Apply(IDevice device, string requested, bool restore)
        {
            if (!device.Enabled) throw new InvalidOperationException("Адаптер выключен. Сначала включите его в настройках Windows.");
            if (!restore && (!Mac.Valid(requested) || (Convert.ToByte(requested.Substring(0, 2), 16) & 3) != 2))
                throw new ArgumentException("Нужен локальный одноадресный MAC из 12 шестнадцатеричных символов.");
            Setting before = device.ReadSetting();
            string beforeMac = Mac.Normalize(device.ReadMac());
            Setting baseline = device.LoadBaseline();
            if (restore && baseline == null) throw new InvalidOperationException("Для этого адаптера ещё нет сохранённой исходной настройки.");
            bool created = baseline == null;
            if (created) device.SaveBaseline(before); // Persist before touching the driver.
            Setting target = restore ? baseline : new Setting(true, requested);
            try
            {
                device.WriteSetting(target);
                device.Restart();
                string current = device.WaitMac(restore ? (target.Exists && Mac.Valid(target.Value) ? Mac.Normalize(target.Value) : null) : requested);
                if (!device.Enabled || !Mac.Valid(current)) throw new InvalidOperationException("Адаптер не вернулся в рабочее состояние.");
                Setting actual = device.ReadSetting();
                if (actual.Exists != target.Exists || (actual.Exists && actual.Value != target.Value))
                    throw new InvalidOperationException("Драйвер не сохранил настройку MAC.");
                if (restore) device.DeleteBaseline();
                return Mac.Format(current);
            }
            catch (Exception failure)
            {
                try
                {
                    device.WriteSetting(before);
                    device.Restart();
                    device.WaitMac(Mac.Valid(beforeMac) ? beforeMac : null);
                    if (!device.Enabled) throw new InvalidOperationException("Адаптер остался выключен.");
                    if (created) device.DeleteBaseline();
                }
                catch (Exception rollback)
                {
                    throw new InvalidOperationException("Изменение не завершено: " + failure.Message +
                        "\r\nАвтоматический откат тоже не завершён: " + rollback.Message +
                        "\r\nНажмите «Вернуть исходную настройку». Если сеть выключена, включите адаптер в настройках Windows. Исходная настройка сохранена.");
                }
                throw new InvalidOperationException("Изменение не применилось. Предыдущий MAC восстановлен.\r\n" + failure.Message);
            }
        }
    }

    public sealed class Adapter
    {
        public Guid Id;
        public string Name, Description, Address, RegistryPath;
        public bool Enabled, DefaultRoute;
        public override string ToString() { return Name + (DefaultRoute ? "  • основной" : "") + (!Enabled ? "  • выключен" : ""); }
    }

    public sealed class WindowsDevice : IDevice
    {
        const string ClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";
        const string BackupRoot = @"SOFTWARE\MacRandomizer\Backups";
        readonly Adapter adapter;
        public WindowsDevice(Adapter adapter) { this.adapter = adapter; }
        static RegistryKey Machine() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }
        public static List<Adapter> List()
        {
            var result = new List<Adapter>();
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            using (var machine = Machine())
            using (var root = machine.OpenSubKey(ClassPath))
            using (var search = new ManagementObjectSearcher("SELECT GUID, NetConnectionID, Name, PhysicalAdapter, NetEnabled FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
            using (var items = search.Get())
            {
                if (root == null) return result;
                var paths = new Dictionary<Guid, string>();
                foreach (string sub in root.GetSubKeyNames())
                {
                    if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                    using (var key = root.OpenSubKey(sub))
                    {
                        Guid id;
                        if (key != null && Guid.TryParse(Convert.ToString(key.GetValue("NetCfgInstanceId")), out id)) paths[id] = ClassPath + "\\" + sub;
                    }
                }
                foreach (ManagementObject item in items)
                using (item)
                {
                    Guid id;
                    if (!Guid.TryParse(Convert.ToString(item["GUID"]), out id) || !paths.ContainsKey(id)) continue;
                    var nic = interfaces.FirstOrDefault(n => SameId(n.Id, id));
                    if (nic == null || (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 && nic.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet && nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetT)) continue;
                    bool route = false;
                    try { route = nic.OperationalStatus == OperationalStatus.Up && nic.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)); } catch (NetworkInformationException) { }
                    result.Add(new Adapter { Id = id, Name = Convert.ToString(item["NetConnectionID"]), Description = Convert.ToString(item["Name"]), Address = nic.GetPhysicalAddress().ToString(), RegistryPath = paths[id], Enabled = Convert.ToBoolean(item["NetEnabled"]), DefaultRoute = route });
                }
            }
            return result.OrderByDescending(a => a.DefaultRoute).ThenByDescending(a => a.Enabled).ThenBy(a => a.Name).ToList();
        }
        static bool SameId(string value, Guid id) { Guid other; return Guid.TryParse(value, out other) && other == id; }
        ManagementObject Find()
        {
            using (var search = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE GUID='" + adapter.Id.ToString("B") + "'"))
            using (var items = search.Get())
                foreach (ManagementObject item in items) return item;
            throw new InvalidOperationException("Адаптер больше не найден. Обновите список.");
        }
        public bool Enabled { get { using (var item = Find()) return Convert.ToBoolean(item["NetEnabled"]); } }
        RegistryKey OpenAdapter(bool writable)
        {
            using (var machine = Machine())
            {
                var key = machine.OpenSubKey(adapter.RegistryPath, writable);
                Guid id;
                if (key == null) throw new InvalidOperationException("Не найден раздел настроек адаптера.");
                if (!Guid.TryParse(Convert.ToString(key.GetValue("NetCfgInstanceId")), out id) || id != adapter.Id)
                { key.Dispose(); throw new InvalidOperationException("Идентификатор адаптера изменился. Обновите список."); }
                return key;
            }
        }
        public Setting ReadSetting()
        {
            using (var key = OpenAdapter(false))
            {
                bool exists = key.GetValueNames().Any(n => string.Equals(n, "NetworkAddress", StringComparison.OrdinalIgnoreCase));
                if (exists && key.GetValueKind("NetworkAddress") != RegistryValueKind.String)
                    throw new InvalidOperationException("Необычный тип NetworkAddress: изменение отменено для сохранения исходных данных.");
                return new Setting(exists, Convert.ToString(key.GetValue("NetworkAddress", "", RegistryValueOptions.DoNotExpandEnvironmentNames)));
            }
        }
        public void WriteSetting(Setting setting)
        {
            using (var key = OpenAdapter(true))
            {
                if (setting.Exists) key.SetValue("NetworkAddress", setting.Value, RegistryValueKind.String);
                else key.DeleteValue("NetworkAddress", false);
                key.Flush();
            }
        }
        string BackupPath { get { return BackupRoot + "\\" + adapter.Id.ToString("D"); } }
        public Setting LoadBaseline()
        {
            using (var machine = Machine())
            using (var key = machine.OpenSubKey(BackupPath))
            {
                if (key == null) return null;
                if (Convert.ToInt32(key.GetValue("Complete", 0)) != 1) throw new InvalidOperationException("Резервная копия неполна. Изменение отменено.");
                return new Setting(Convert.ToInt32(key.GetValue("Exists", 0)) == 1, Convert.ToString(key.GetValue("Value", "")));
            }
        }
        public void SaveBaseline(Setting setting)
        {
            using (var machine = Machine())
            using (var key = machine.CreateSubKey(BackupPath))
            {
                key.SetValue("Value", setting.Value, RegistryValueKind.String);
                key.SetValue("Exists", setting.Exists ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Complete", 1, RegistryValueKind.DWord);
                key.Flush();
            }
        }
        public void DeleteBaseline() { using (var machine = Machine()) machine.DeleteSubKeyTree(BackupPath, false); }
        void Invoke(string method)
        {
            using (var item = Find())
            {
                item.Options.Timeout = TimeSpan.FromSeconds(25);
                uint code = Convert.ToUInt32(item.InvokeMethod(method, null));
                if (code != 0) throw new InvalidOperationException("Windows: " + method + ", код " + code + ".");
            }
        }
        public void Restart()
        {
            try { Invoke("Disable"); }
            finally { Invoke("Enable"); } // Always attempt to bring the selected adapter back.
        }
        public string ReadMac()
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => SameId(n.Id, adapter.Id));
            return nic == null ? "" : Mac.Normalize(nic.GetPhysicalAddress().ToString());
        }
        public string WaitMac(string expected)
        {
            for (int i = 0; i < 40; i++)
            {
                string current = ReadMac();
                if (Mac.Valid(current) && (expected == null || current == Mac.Normalize(expected)) && Enabled) return current;
                Thread.Sleep(500);
            }
            throw new InvalidOperationException(expected == null ? "Не удалось прочитать MAC после перезапуска." : "Драйвер не подтвердил MAC " + Mac.Format(expected) + ". Возможно, эта карта не поддерживает смену адреса.");
        }
    }

    public sealed class DarkButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Enabled) return;
            using (var brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(151, 167, 188), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    public sealed class MainForm : Form
    {
        readonly ComboBox adapters = new ComboBox();
        readonly Label description = new Label(), address = new Label(), saved = new Label(), status = new Label();
        readonly Button change = new DarkButton(), restore = new DarkButton(), refresh = new DarkButton();
        readonly ProgressBar progress = new ProgressBar();
        readonly bool demo;
        bool busy;
        static readonly Color Background = Color.FromArgb(18, 23, 33), Card = Color.FromArgb(28, 35, 48), Muted = Color.FromArgb(168, 183, 205), Accent = Color.FromArgb(99, 228, 189);
        public MainForm(bool demo)
        {
            this.demo = demo;
            Text = "MAC Randomizer"; ClientSize = new Size(680, 560); MinimumSize = Size; MaximumSize = Size;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background; ForeColor = Color.White; Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            Label title = LabelAt("Новый MAC. Один клик.", 30, 26, 620, 44, 25, Color.White);
            title.Font = new Font("Segoe UI", 25, FontStyle.Bold);
            LabelAt("Случайный адрес для выбранного сетевого адаптера", 32, 78, 620, 25, 11, Muted);
            LabelAt("СЕТЕВОЙ АДАПТЕР", 32, 124, 430, 24, 9, Muted);
            adapters.SetBounds(32, 152, 464, 32); adapters.DropDownStyle = ComboBoxStyle.DropDownList;
            adapters.BackColor = Card; adapters.ForeColor = Color.White; adapters.FlatStyle = FlatStyle.Flat;
            Controls.Add(adapters); adapters.SelectedIndexChanged += delegate { UpdateSelected(); };
            StyleButton(refresh, "Обновить", 508, 151, 140, 35, false); refresh.Click += async delegate { await LoadAdapters(); };
            description.SetBounds(32, 195, 616, 43); description.ForeColor = Muted; Controls.Add(description);
            Panel panel = new Panel { BackColor = Card }; panel.SetBounds(32, 248, 616, 108); Controls.Add(panel);
            var label = new Label { Text = "ТЕКУЩИЙ MAC", ForeColor = Muted }; label.SetBounds(18, 12, 400, 23); panel.Controls.Add(label);
            address.SetBounds(16, 37, 580, 42); address.Font = new Font("Consolas", 26, FontStyle.Bold); address.ForeColor = Accent; panel.Controls.Add(address);
            saved.SetBounds(32, 366, 616, 27); saved.ForeColor = Muted; saved.Font = new Font("Segoe UI", 9); Controls.Add(saved);
            StyleButton(change, "Случайный MAC", 32, 406, 300, 49, true);
            StyleButton(restore, "Вернуть исходную настройку", 344, 406, 304, 49, false);
            change.Click += async delegate { await Apply(false); }; restore.Click += async delegate { await Apply(true); };
            LabelAt("При смене адреса соединение временно прервётся.", 32, 467, 616, 24, 9, Muted);
            status.SetBounds(32, 497, 616, 42); status.Font = new Font("Segoe UI", 9); status.ForeColor = Muted; Controls.Add(status);
            progress.SetBounds(32, 545, 616, 3); progress.Style = ProgressBarStyle.Marquee; progress.Visible = false; Controls.Add(progress);
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "Дождитесь окончания операции — адаптер должен снова включиться."; } };
            Shown += async delegate { await LoadAdapters(); };
            if (demo) LoadDemo();
        }
        Label LabelAt(string text, int x, int y, int w, int h, float size, Color color)
        {
            var label = new Label { Text = text, ForeColor = color, Font = new Font("Segoe UI", size) };
            label.SetBounds(x, y, w, h); Controls.Add(label); return label;
        }
        void StyleButton(Button button, string text, int x, int y, int w, int h, bool primary)
        {
            button.Text = text; button.SetBounds(x, y, w, h); button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0; button.BackColor = primary ? Accent : Card;
            button.ForeColor = primary ? Background : Color.White; button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            button.Cursor = Cursors.Hand; Controls.Add(button);
        }
        void LoadDemo()
        {
            adapters.Items.Clear(); adapters.Items.Add(new Adapter { Id = Guid.Empty, Name = "Wi-Fi", Description = "Пример интерфейса • демонстрационные данные", Address = "024A8C19E3B7", Enabled = true, DefaultRoute = true });
            adapters.SelectedIndex = 0; status.Text = "Демонстрация. Настройки сети не изменяются."; change.Enabled = false; restore.Enabled = false;
        }
        void Busy(bool value, string message)
        {
            busy = value; adapters.Enabled = refresh.Enabled = !value; progress.Visible = value;
            change.Enabled = restore.Enabled = false; status.Text = message; status.ForeColor = Muted;
            if (!value) UpdateSelected();
        }
        async Task LoadAdapters()
        {
            if (demo) { LoadDemo(); return; }
            var old = adapters.SelectedItem as Adapter;
            Busy(true, "Ищу сетевые адаптеры…");
            try
            {
                var list = await Task.Run(() => WindowsDevice.List());
                adapters.Items.Clear(); foreach (var a in list) adapters.Items.Add(a);
                adapters.SelectedItem = old == null ? null : list.FirstOrDefault(a => a.Id == old.Id);
                if (adapters.SelectedIndex < 0 && list.Count > 0) adapters.SelectedIndex = 0;
                status.Text = list.Count == 0 ? "Физические адаптеры Wi-Fi / Ethernet не найдены." : "Готово. Выберите адаптер и нажмите «Случайный MAC».";
            }
            catch (Exception e) { status.Text = "Не удалось получить список адаптеров."; ShowError(e); }
            finally { string message = status.Text; Busy(false, message); }
        }
        void UpdateSelected()
        {
            if (busy) return;
            var a = adapters.SelectedItem as Adapter;
            description.Text = a == null ? "" : a.Description; address.Text = a == null ? "—" : Mac.Format(a.Address);
            change.Enabled = !demo && a != null && a.Enabled && Mac.Valid(a.Address);
            restore.Enabled = false; saved.Text = "Исходная настройка сохраняется перед первым изменением.";
            if (a == null || demo) return;
            try
            {
                bool exists = new WindowsDevice(a).LoadBaseline() != null;
                saved.Text = exists ? "Исходная настройка сохранена. Её можно вернуть в любой момент." : saved.Text;
                restore.Enabled = exists && a.Enabled;
            }
            catch (Exception e) { saved.Text = e.Message; change.Enabled = false; }
        }
        async Task Apply(bool isRestore)
        {
            var a = adapters.SelectedItem as Adapter; if (a == null || busy || demo) return;
            Busy(true, "Перезапускаю выбранный адаптер. Дождитесь завершения…");
            string resultMessage = ""; bool success = false;
            try
            {
                string random; do { random = Mac.Generate(); } while (random == Mac.Normalize(a.Address));
                string result = await Task.Run(() => ChangeEngine.Apply(new WindowsDevice(a), random, isRestore));
                resultMessage = (isRestore ? "Исходная настройка восстановлена. MAC: " : "MAC успешно изменён: ") + result;
                success = true;
            }
            catch (Exception e) { resultMessage = "Операция не завершена. Подробности — в сообщении об ошибке."; ShowError(e); }
            finally { Busy(false, resultMessage); }
            await LoadAdapters();
            status.Text = resultMessage; status.ForeColor = success ? Accent : Color.Salmon;
        }
        void ShowError(Exception e) { MessageBox.Show(this, e.Message, "MAC Randomizer", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--self-test")
            {
                try { File.WriteAllText(args[1], Tests.Run(), new UTF8Encoding(true)); return 0; }
                catch (Exception e) { File.WriteAllText(args[1], "FAIL: " + e, new UTF8Encoding(true)); return 1; }
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--render-preview")
            {
                using (var form = new MainForm(true))
                {
                    form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(args[1]); }
                    form.Close();
                }
                return 0;
            }
            if (args.Length == 1 && args[0] == "--demo") { Application.Run(new MainForm(true)); return 0; }
            // One instance prevents overlapping registry changes and adapter restarts.
            using (var mutex = new Mutex(false, @"Global\MacRandomizer.Application.v1"))
            {
                bool owned;
                try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) { MessageBox.Show("MAC Randomizer уже запущен."); return 0; }
                bool admin;
                using (var identity = WindowsIdentity.GetCurrent()) admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                if (!admin)
                {
                    // Release before starting the elevated instance.
                    mutex.ReleaseMutex();
                    try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory }); }
                    catch (Win32Exception e) { if (e.NativeErrorCode != 1223) MessageBox.Show(e.Message); }
                    return 0;
                }
                Application.Run(new MainForm(false));
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }

    static class Tests
    {
        sealed class Fake : IDevice
        {
            public Setting Current = new Setting(false, ""), Backup;
            public string Address = "001122334455";
            public bool Accept = true, On = true, FailRestartOnce, FailRollback;
            public int Restarts;
            public bool Enabled { get { return On; } }
            public Setting ReadSetting() { return Current; }
            public void WriteSetting(Setting value) { if (FailRollback && Restarts > 0) throw new Exception("rollback failure"); Current = value; }
            public Setting LoadBaseline() { return Backup; }
            public void SaveBaseline(Setting value) { Backup = value; }
            public void DeleteBaseline() { Backup = null; }
            public void Restart() { Restarts++; if (FailRestartOnce && Restarts == 1) throw new Exception("restart failure"); if (Accept) Address = Current.Exists ? Current.Value : "001122334455"; }
            public string ReadMac() { return Address; }
            public string WaitMac(string expected) { if (expected != null && Address != expected) throw new Exception("driver rejected MAC"); return Address; }
        }
        static void Check(bool value, string name) { if (!value) throw new Exception(name); }
        static string Error(Action action) { try { action(); } catch (InvalidOperationException e) { return e.Message; } throw new Exception("Expected rejection"); }
        public static string Run()
        {
            var lines = new List<string>();
            var seen = new HashSet<string>();
            for (int i = 0; i < 10000; i++) { string mac = Mac.Generate(); Check(Mac.Valid(mac) && mac.StartsWith("02") && seen.Add(mac), "MAC generation"); }
            lines.Add("PASS: 10,000 random local unicast MAC addresses; no duplicates");
            var d = new Fake(); ChangeEngine.Apply(d, "02AABBCCDDEE", false); ChangeEngine.Apply(d, "021234567890", false);
            Check(d.Address == "021234567890" && !d.Backup.Exists, "baseline preserved");
            ChangeEngine.Apply(d, null, true); Check(!d.Current.Exists && d.Backup == null && d.Address == "001122334455", "restore absent override");
            lines.Add("PASS: repeated changes preserve original; restore removes originally absent override");
            d = new Fake { Current = new Setting(true, "02A1B2C3D4E5"), Address = "02A1B2C3D4E5" };
            ChangeEngine.Apply(d, "02AABBCCDDEE", false); ChangeEngine.Apply(d, null, true);
            Check(d.Current.Value == "02A1B2C3D4E5", "restore custom override"); lines.Add("PASS: pre-existing custom override restored exactly");
            d = new Fake { Accept = false }; string error = Error(() => ChangeEngine.Apply(d, "02AABBCCDDEE", false));
            Check(!d.Current.Exists && d.Backup == null && d.Restarts == 2 && error.Contains("восстановлен"), "driver rollback");
            lines.Add("PASS: driver rejection rolls back registry and restarts adapter");
            d = new Fake { FailRestartOnce = true }; Error(() => ChangeEngine.Apply(d, "02AABBCCDDEE", false));
            Check(!d.Current.Exists && d.Backup == null && d.Restarts == 2, "restart rollback"); lines.Add("PASS: restart failure triggers rollback");
            d = new Fake { Accept = false, FailRollback = true }; error = Error(() => ChangeEngine.Apply(d, "02AABBCCDDEE", false));
            Check(d.Backup != null && error.Contains("откат тоже"), "retained recovery"); lines.Add("PASS: failed rollback retains recovery backup and reports failure");
            d = new Fake { On = false }; Error(() => ChangeEngine.Apply(d, "02AABBCCDDEE", false)); Check(d.Restarts == 0 && d.Backup == null, "disabled untouched");
            d = new Fake(); Error(() => ChangeEngine.Apply(d, null, true)); Check(d.Restarts == 0, "missing backup");
            lines.Add("PASS: disabled adapters and restore without backup do not mutate settings");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
    }
}
