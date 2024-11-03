using System.Windows;
using System.Windows.Controls;
using System.IO.Ports;
using System.Management;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

using Newtonsoft.Json;
using Microsoft.Win32;

namespace FanGuardianWpf
{
    public class GithubRelease
    {
        [JsonProperty("tag_name")]
        public string TagName { get; set; }
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // FanGuardian controller
        string controllerVID = "VID_2886";
        string controllerPID = "PID_802F";
        string controllerBootloaderPID = "PID_002F";

        string displayVID = "VID_303A";
        string displayPID = "PID_1001";

        static string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        static string binariesDirectory = System.IO.Path.Combine(baseDirectory, "binaries");
        static string bossacExePath = System.IO.Path.Combine(binariesDirectory, "bossac.exe");
        static string esptoolExePath = System.IO.Path.Combine(binariesDirectory, "esptool.exe");
        static string firmwareDir = System.IO.Path.Combine(baseDirectory, "firmwares");
        static string controllerFirmwareFileBaseName = "controller-firmware.bin";
        static string displayFirmwareFileBaseName = "fanguardian-ui";
        static string? filePathCustContrFw;
        static string? filePathCustDispFw;


        const string controllerReleasesUrl = "https://api.github.com/repos/SingularityComputers/FanGuardianController/releases";
        const string displayReleasesUrl = "https://api.github.com/repos/SingularityComputers/FanGuardianUI/releases";


        public MainWindow()
        {
            InitializeComponent();

            if (!Directory.Exists(firmwareDir))
            {
                Directory.CreateDirectory(firmwareDir);
            }

            LoadVersionsAsync();
        }

        private async void LoadVersionsAsync()
        {
            List<string> versions = await FetchControllerVersionsAsync(controllerReleasesUrl);
            ControllerVersionComboBox.ItemsSource = versions;

            versions = await FetchControllerVersionsAsync(displayReleasesUrl);
            DisplayVersionComboBox.ItemsSource = versions;
        }

        private async Task<List<string>> FetchControllerVersionsAsync(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                // Set the User-Agent to avoid 403 Forbidden error
                client.DefaultRequestHeaders.Add("User-Agent", "C# App");

                var response = await client.GetStringAsync(url);
                var releases = JsonConvert.DeserializeObject<List<GithubRelease>>(response);

                List<string> versionList = new List<string>();

                foreach (var release in releases)
                {
                    versionList.Add(release.TagName);
                }

                return versionList;
            }
        }

        private async Task DownloadFirmware(string selectedRepo, string selectedVersion, string fileName, TextBox LogTextBox)
        {
            string firmwareUrl = $"https://github.com/SingularityComputers/{selectedRepo}/releases/download/{selectedVersion}/{fileName}";

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "C# App");
                    byte[] firmwareData = await client.GetByteArrayAsync(firmwareUrl);

                    // Save the firmware.bin file
                    string filePath = System.IO.Path.Combine(firmwareDir, $"{selectedVersion}-{fileName}");
                    System.IO.File.WriteAllBytes(filePath, firmwareData);

                    LogTextBox.AppendText($"Firmware downloaded to {filePath}\r\n");
                    LogTextBox.ScrollToEnd();
                }
            }
            catch (Exception ex)
            {
                LogTextBox.AppendText($"Error downloading firmware: {firmwareUrl} {ex.Message}\r\n");
                LogTextBox.ScrollToEnd();
                throw;
            }
        }


        string GetComPortByVidPid(string vid, string pid)
        {
            string query = "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%" + vid + "&" + pid + "%'";

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    string deviceId = device["DeviceID"]?.ToString();
                    string caption = device["Caption"]?.ToString();

                    if (caption != null && caption.Contains("(COM"))
                    {
                        // Extract COM port from caption
                        string comPort = caption.Substring(caption.LastIndexOf("(COM")).Replace("(", "").Replace(")", "");
                        return comPort;
                    }
                }
            }
            return null;
        }

        async Task RunWithBOSSAC(string port, string firmwarePath)
        {
            ControllerFlashButton.IsEnabled = false;

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.WorkingDirectory = binariesDirectory;
            startInfo.FileName = bossacExePath;
            startInfo.Arguments = $"--port={port} -U true -i -e -w -v -b \"{firmwarePath}\" -R";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            ControllerLogTextBox.AppendText($"Firmware update started...\r\n");
            ControllerLogTextBox.AppendText($"{startInfo.FileName} {startInfo.Arguments}\r\n");

            Process process = new Process
            {
                StartInfo = startInfo,
            };

            process.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        ControllerLogTextBox.AppendText($"{args.Data}\r\n");
                        ControllerLogTextBox.ScrollToEnd();
                    });
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        ControllerLogTextBox.AppendText($"{args.Data}\r\n");
                        ControllerLogTextBox.ScrollToEnd();
                    });
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await Task.Run(() => process.WaitForExit());
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ControllerLogTextBox.AppendText("Firmware update failed!\r\n");
                    ControllerLogTextBox.ScrollToEnd();
                });
            }
            finally
            {
                ControllerFlashButton.IsEnabled = true;
            }
        }

        async Task RunWithESPTool(string firmwarePath)
        {
            DisplayFlashButton.IsEnabled = false;

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.WorkingDirectory = binariesDirectory;
            startInfo.FileName = esptoolExePath;
            startInfo.Arguments = $"write_flash 0x0 \"{firmwarePath}\"";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            DisplayLogTextBox.AppendText($"Firmware update started...\r\n");
            DisplayLogTextBox.AppendText($"{startInfo.FileName} {startInfo.Arguments}\r\n");

            Process process = new Process
            {
                StartInfo = startInfo,
            };

            process.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        DisplayLogTextBox.AppendText($"{args.Data}\r\n");
                        DisplayLogTextBox.ScrollToEnd();
                    });
                }
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        DisplayLogTextBox.AppendText($"{args.Data}\r\n");
                        DisplayLogTextBox.ScrollToEnd();
                    });
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await Task.Run(() => process.WaitForExit());
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    DisplayLogTextBox.AppendText("Firmware update failed!\r\n");
                    DisplayLogTextBox.ScrollToEnd();
                });
            }
            finally
            {
                DisplayFlashButton.IsEnabled = true;
            }
        }

        private async Task runControllerUpdate()
        {
            string? selectedVersion = ControllerVersionComboBox.SelectedItem as string;

            try
            {
                await DownloadFirmware("FanGuardianController", selectedVersion, controllerFirmwareFileBaseName, ControllerLogTextBox);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Firmware download failed. Operation canceled.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string comPortA = GetComPortByVidPid(controllerVID, controllerPID);
            string comPortB = GetComPortByVidPid(controllerVID, controllerBootloaderPID);

            if (!string.IsNullOrEmpty(comPortA))
            {
                // Enter bootloader uploader mode
                using (SerialPort serialPort = new SerialPort(comPortA, 1200))
                {
                    serialPort.Open();
                    serialPort.DtrEnable = true;
                    serialPort.DtrEnable = false;

                    // Close the serial port after a short delay
                    System.Threading.Thread.Sleep(100);
                    serialPort.Close();
                }

                System.Threading.Thread.Sleep(500);
                comPortB = GetComPortByVidPid(controllerVID, controllerBootloaderPID);
                await RunWithBOSSAC(comPortB, $"{firmwareDir}/{selectedVersion}-{controllerFirmwareFileBaseName}");
            }
            else if (!string.IsNullOrEmpty(comPortB))
            {
                await RunWithBOSSAC(comPortB, $"{firmwareDir}/{selectedVersion}-{controllerFirmwareFileBaseName}");
            }
            else
            {
                MessageBox.Show("FanGuardian controller device not connected.");
            }
        }

        // For custom firmware on Controller
        private async Task runControllerUpdateCustom()
        {
            string comPortA = GetComPortByVidPid(controllerVID, controllerPID);
            string comPortB = GetComPortByVidPid(controllerVID, controllerBootloaderPID);

            if (!string.IsNullOrEmpty(comPortA))
            {
                // Enter bootloader uploader mode
                using (SerialPort serialPort = new SerialPort(comPortA, 1200))
                {
                    serialPort.Open();
                    serialPort.DtrEnable = true;
                    serialPort.DtrEnable = false;

                    // Close the serial port after a short delay
                    System.Threading.Thread.Sleep(100);
                    serialPort.Close();
                }

                System.Threading.Thread.Sleep(500);
                comPortB = GetComPortByVidPid(controllerVID, controllerBootloaderPID);
                if (string.IsNullOrEmpty(filePathCustContrFw))
                {
                    MessageBox.Show("No custom firmware selected!");
                }
                else
                {
                    await RunWithBOSSAC(comPortB, $"{filePathCustContrFw}");
                }
            }
            else if (!string.IsNullOrEmpty(comPortB))
            {
                if (string.IsNullOrEmpty(filePathCustContrFw))
                {
                    MessageBox.Show("No custom firmware selected!");
                }
                else
                {
                    await RunWithBOSSAC(comPortB, $"{filePathCustContrFw}");
                }
            }
            else
            {
                MessageBox.Show("FanGuardian controller device not connected.");
            }
        }


        private async Task runDisplayUpdate()
        {
            string? selectedVersion = DisplayVersionComboBox.SelectedItem as string;
            string version = selectedVersion?.Substring(selectedVersion.Length - 8) ?? "00000000";

            try
            {
                await DownloadFirmware("FanGuardianUI", selectedVersion, $"{displayFirmwareFileBaseName}-{version}.bin", DisplayLogTextBox);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Firmware download failed. Operation canceled.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string comPortA = GetComPortByVidPid(displayVID, displayPID);

            if (!string.IsNullOrEmpty(comPortA))
            {
                await RunWithESPTool($"{firmwareDir}/{selectedVersion}-{displayFirmwareFileBaseName}-{version}.bin");
            }
            else
            {
                MessageBox.Show("FanGuardian display device not connected.");
            }
        }

        // For custom firmware on Display
        private async Task runDisplayUpdateCustom()
        {

            string comPortA = GetComPortByVidPid(displayVID, displayPID);

            if (!string.IsNullOrEmpty(comPortA))
            {
                if (string.IsNullOrEmpty(filePathCustDispFw))
                {
                    MessageBox.Show("No custom firmware selected!");
                }
                else
                {
                    await RunWithESPTool($"{filePathCustDispFw}");
                }
            }
            else
            {
                MessageBox.Show("FanGuardian display device not connected.");
            }
        }


        private async void ControllerFlashButton_Click(object sender, RoutedEventArgs e)
        {
            if (ControllerOfficialRb.IsChecked == true)
            {
                ControllerLogTextBox.Text = "";
                await runControllerUpdate();
            }
            else await runControllerUpdateCustom();
        }

        private async void DisplayFlashButton_Click(object sender, RoutedEventArgs e)
        {
            if (DisplayOfficialRb.IsChecked == true)
            {
                DisplayLogTextBox.Text = "";
                await runDisplayUpdate();
            }
            else await runDisplayUpdateCustom();
        }

        private void ControllerRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (ControllerOfficialRb.IsChecked == true)
            {
                ControllerVersionComboBox.IsEnabled = true;
                ControllerCustomFirmwareBrowseButton.IsEnabled = false;
            }
            else
            {
                ControllerVersionComboBox.IsEnabled = false;
                ControllerVersionComboBox.SelectedItem = null;
                ControllerCustomFirmwareBrowseButton.IsEnabled = true;
                MessageBox.Show("You are about to flash a custom firmware to the FanGuardian controller! \r\nYou are doing this at your own risk! This may void the warranty.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void DisplayRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (DisplayOfficialRb.IsChecked == true)
            {
                DisplayVersionComboBox.IsEnabled = true;
                DisplayCustomFirmwareBrowseButton.IsEnabled = false;
            }
            else
            {
                DisplayVersionComboBox.IsEnabled = false;
                DisplayVersionComboBox.SelectedItem = null;
                DisplayCustomFirmwareBrowseButton.IsEnabled = true;
                MessageBox.Show("You are about to flash a custom firmware to the FanGuardian display! \r\nYou are doing this at your own risk! This may void the warranty.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ControllerCustomFirmwareBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            openFileDialog.Title = "Select a binary file";
            openFileDialog.Filter = "Binary Data (*.bin)|*.bin";

            if (openFileDialog.ShowDialog() == true)
            {
                filePathCustContrFw = openFileDialog.FileName;
                ControllerFirmwareNameLabel.Content = "Custom firmware selected: " + System.IO.Path.GetFileName(filePathCustContrFw);
                MessageBox.Show($"File selected for controller update: \r\n {filePathCustContrFw}", "File Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void DisplayCustomFirmwareBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();

            openFileDialog.Title = "Select a binary file";
            openFileDialog.Filter = "Binary Data (*.bin)|*.bin";

            if (openFileDialog.ShowDialog() == true)
            {
                filePathCustDispFw = openFileDialog.FileName;
                DisplayFirmwareNameLabel.Content = "Custom firmware selected:  " + System.IO.Path.GetFileName(filePathCustDispFw);
                MessageBox.Show($"File selected for display update: \r\n {filePathCustDispFw}", "File Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ControllerVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ControllerFirmwareNameLabel.Content = string.Empty;
        }

        private void DisplayVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DisplayFirmwareNameLabel.Content = string.Empty;
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Clipboard.SetText(e.Uri.AbsoluteUri);

            MessageBox.Show("Link copied to clipboard: " + e.Uri.AbsoluteUri);

            e.Handled = true;
        }
    }
}