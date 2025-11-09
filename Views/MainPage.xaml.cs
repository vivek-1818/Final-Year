using DeezFiles.Models;
using DeezFiles.Services;
using DeezFiles.Utilities;
using DNStore.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
namespace DeezFiles
{
    public partial class MainPage : Page
    {
        private ObservableCollection<FileItem> Files { get; } = new ObservableCollection<FileItem>();

        // Constructor
        public MainPage()
        {
            InitializeComponent();
            this.Loaded += MainPage_Loaded; // Register the Loaded event
            LoadFileStateAndPopulateAsync(); // Load file state when the page is loaded
        }

        /// <summary>
        /// Reads filestate.json (if it exists & is non‐empty) and rebuilds the rows in the UI.
        /// </summary>
        private async Task LoadFileStateAndPopulateAsync()
        {
            try
            {
                // 1) Clear any existing rows
                RowsPanel.Children.Clear();

                // 2) Compute path to your JSON
                string jsonFilePath = Path.Combine(LocalFileHelper.statePath, "filestate.json"); // Stringed out so that it will work on my system where statepath is not declared or idk
                //string jsonFilePath = "C:\\Users\\user\\OneDrive\\Documents\\DNStore\\DN_Test\\state\\filestate.json";

                // 3) If the file isn't there, bail out
                if (!File.Exists(jsonFilePath))
                    return;
                    

                // 4) Read the entire file
                string jsonData = await File.ReadAllTextAsync(jsonFilePath);

                // 5) If the file is empty or whitespace, do nothing
                if (string.IsNullOrWhiteSpace(jsonData))
                    return;

                // 6) Deserialize into a dictionary
                var fileStateDict = JsonSerializer
                    .Deserialize<Dictionary<string, JsonElement>>(jsonData);

                ulong totalSize = 0;

                // 7) For each entry, extract fields & add a row
                foreach (var kvp in fileStateDict)
                {
                    string fileName = kvp.Key;
                    var fileData = kvp.Value;

                    DateTime uploadTime = fileData.GetProperty("UploadTime").GetDateTime();
                    ulong fileSize = fileData.GetProperty("Size").GetUInt64();
                    totalSize += fileSize;

                    string formattedSize = FormatFileSize(fileSize);

                    // Build the UI row and add it
                    var row = CreateFileRow(fileName, uploadTime, formattedSize);
                    RowsPanel.Children.Add(row);
                }

                // 8) Update your totals labels
                TotalFilesCount.Text = $"Total Files: {fileStateDict.Count}";
                UploadSizeTotal.Text = FormatFileSize(totalSize);
            }
            catch (Exception ex)
            {
                // Log any problems but don’t crash the UI
                System.Diagnostics.Debug.WriteLine($"Error loading filestate.json: {ex}");
            }
        }

        public Grid CreateFileRow(string fileName, DateTime uploadTime, string size)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // File Name - Changed to White
            var tbName = new TextBlock
            {
                Margin = new Thickness(30, 0, 0, 0),
                Text = fileName,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Colors.White) // Added white color
            };
            Grid.SetColumn(tbName, 0);
            row.Children.Add(tbName);

            // Date - Changed to White
            var tbDate = new TextBlock
            {
                Margin = new Thickness(15, 0, 0, 0),
                Text = uploadTime.ToString("MM/dd/yyyy HH:mm"),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White) // Added white color
            };
            Grid.SetColumn(tbDate, 1);
            row.Children.Add(tbDate);

            // Size - Changed to White
            var tbSize = new TextBlock
            {
                Margin = new Thickness(0, 0, 30, 0),
                Text = size,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White) // Added white color
            };
            Grid.SetColumn(tbSize, 2);
            row.Children.Add(tbSize);

            // Download Button - Replaced text with PNG image
            var btnDownload = new Button
            {
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(5, 0, 20, 0), // Added right margin for symmetry
                Tag = fileName, // store file name to identify which file to download
                Background = Brushes.Transparent, // Make button background transparent
                BorderBrush = Brushes.Transparent, // Remove button border
                HorizontalAlignment = HorizontalAlignment.Center, // Center the button in its column
                Cursor = Cursors.Hand, // Change cursor to hand on hover
                Content = new Image
                {
                    Source = new BitmapImage(new Uri("/Assets/Download.png", UriKind.Relative)),
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Fill
                }
            };
            btnDownload.Click += Download_Click;
            Grid.SetColumn(btnDownload, 3);
            row.Children.Add(btnDownload);

            return row;
        }


        // Loaded event handler
        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            string addinfo = LocalFileHelper.GetDNETaddress();
            string[] add = addinfo.Split(":");
            UserID.Text = add[0];
            UserAdd.Text = "@" + add[1].ToLower();
            Blockchain.InitializeBlockchainAsync();
            UpdateFileList();
            LocalFileHelper.FileListUpdated += OnFileListUpdated;
        }

        private void OnFileListUpdated(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await UpdateFileListAsync();
            }), DispatcherPriority.Normal);
        }

        private async Task UpdateFileListAsync()
        {
            try
            {
                // Clear previous rows
                RowsPanel.Children.Clear();

                string jsonFilePath = Path.Combine(LocalFileHelper.statePath, "filestate.json");

                if (File.Exists(jsonFilePath))
                {
                    string jsonData = await File.ReadAllTextAsync(jsonFilePath);

                    if (!string.IsNullOrEmpty(jsonData))
                    {
                        var fileStateDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData);

                        ulong totalsize = 0;

                        foreach (var kvp in fileStateDict)
                        {
                            string fileName = kvp.Key;
                            var fileData = kvp.Value;

                            DateTime uploadTime = fileData.GetProperty("UploadTime").GetDateTime();
                            ulong fileSize = fileData.GetProperty("Size").GetUInt64();
                            totalsize += fileSize;

                            string formattedSize = FormatFileSize(fileSize);

                            // Create row UI for this file
                            var row = CreateFileRow(fileName, uploadTime, formattedSize);

                            RowsPanel.Children.Add(row);
                        }

                        // Update totals
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            TotalFilesCount.Text = "Total Files: " + fileStateDict.Count.ToString();
                            UploadSizeTotal.Text = FormatFileSize(totalsize);
                        }), DispatcherPriority.Normal);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating file list: {ex.Message}");
            }
        }


        private void UpdateFileList()
        {
            _ = UpdateFileListAsync();
        }

        private string FormatFileSize(ulong bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;

            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }

            return string.Format("{0:n1}{1}", number, suffixes[counter]);
        }

        private void LogOut_Click(object sender, RoutedEventArgs e)
        {
            LocalFileHelper.FileListUpdated -= OnFileListUpdated;
            AuthorizationService.Logout();
            this.NavigationService.Navigate(new Uri("Views/LoginPage.xaml", UriKind.RelativeOrAbsolute));

        }


        private async void Upload_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog();
            openFileDialog.Multiselect = false;

            if (openFileDialog.ShowDialog() == true)
            {
                // Get file info
                var fileInfo = new FileInfo(openFileDialog.FileName);
                string fileName = openFileDialog.SafeFileName;
                DateTime uploadTime = DateTime.Now;
                ulong fileSize = (ulong)fileInfo.Length;
                string formattedSize = FormatFileSize(fileSize);

                // Create the row
                var newRow = CreateFileRow(fileName, uploadTime, formattedSize);

                // Add the row to the UI
                RowsPanel.Children.Add(newRow);

                // Update the totals
                UpdateTotals();

                // Upload the file (keep this at the end)
                await FileHelper.UploadFile(openFileDialog.FileName);
            }
        }

        /// <summary>
        /// Updates the total files count and total size display
        /// </summary>
        private void UpdateTotals()
        {
            try
            {
                // Count current rows in the UI
                int totalFiles = RowsPanel.Children.Count;

                // Calculate total size from all rows
                ulong totalSize = 0;

                foreach (var child in RowsPanel.Children)
                {
                    if (child is Grid row)
                    {
                        // Find the size TextBlock (it's in column 2)
                        foreach (var gridChild in row.Children)
                        {
                            if (gridChild is TextBlock tb && Grid.GetColumn(tb) == 2)
                            {
                                // Parse the size text back to bytes for accurate totaling
                                totalSize += ParseSizeToBytes(tb.Text);
                                break;
                            }
                        }
                    }
                }

                // Update the UI labels
                TotalFilesCount.Text = $"Total Files: {totalFiles}";
                UploadSizeTotal.Text = FormatFileSize(totalSize);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating totals: {ex}");
            }
        }

        /// <summary>
        /// Converts formatted size string back to bytes (helper for UpdateTotals)
        /// </summary>
        private ulong ParseSizeToBytes(string sizeText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sizeText))
                    return 0;

                // Remove any spaces and get the numeric part and suffix
                sizeText = sizeText.Trim();

                string numericPart = "";
                string suffix = "";

                for (int i = 0; i < sizeText.Length; i++)
                {
                    if (char.IsDigit(sizeText[i]) || sizeText[i] == '.')
                    {
                        numericPart += sizeText[i];
                    }
                    else
                    {
                        suffix = sizeText.Substring(i);
                        break;
                    }
                }

                if (!decimal.TryParse(numericPart, out decimal number))
                    return 0;

                // Convert back to bytes based on suffix
                switch (suffix.ToUpper())
                {
                    case "B":
                        return (ulong)number;
                    case "KB":
                        return (ulong)(number * 1024);
                    case "MB":
                        return (ulong)(number * 1024 * 1024);
                    case "GB":
                        return (ulong)(number * 1024 * 1024 * 1024);
                    case "TB":
                        return (ulong)(number * 1024 * 1024 * 1024 * 1024);
                    default:
                        return (ulong)number; // Assume bytes if no suffix
                }
            }
            catch
            {
                return 0;
            }
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button clickedButton && clickedButton.Tag is string fileName)
            {
                await FileHelper.DownloadFile(fileName);
            }
        }


        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        private void FileList_SelectionChanged_1(object sender, SelectionChangedEventArgs e)
        {

        }
    }
}
