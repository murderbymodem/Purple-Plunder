using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using PurplePlunder.Core.Enums;
using PurplePlunder.Core.Models;
using PurplePlunder.Gui.Interfaces;
using PurplePlunder.Services.Interfaces;
using PurplePlunder.Shared.Commands;

namespace PurplePlunder.Gui.ViewModels
{
    public class DownloadsViewModel : ViewModelBase, INavigationState
    {
        #region Fields

        private readonly IDialogService _dialogService;
        private readonly IDownloadService _downloadService;
        private readonly INavigationService _navigationService;

        private ICommand _retryDownloadCommand;
        private ICommand _cancelDownloadCommand;
        private ICommand _removeDownloadCommand;
        private ICommand _showLogCommand;
        private ICommand _openDownloadFolderCommand;

        private readonly object _commandLockObject;

        #endregion Fields

        #region Constructors

        public DownloadsViewModel(
            IDialogService dialogService,
            IDownloadService downloadService,
            INavigationService navigationService)
        {
            _dialogService = dialogService;
            _downloadService = downloadService;
            _navigationService = navigationService;

            _downloadService.PropertyChanged += DownloadService_PropertyChanged;

            _commandLockObject = new object();
        }

        #endregion Constructors

        #region Properties

        public Vector ScrollPosition { get; set; }

        public ObservableCollection<VodDownload> Downloads
        {
            get { return _downloadService.Downloads; }
        }

        public ICommand RetryDownloadCommand
        {
            get
            {
                if (_retryDownloadCommand == null)
                {
                    _retryDownloadCommand = new DelegateCommand<string>(RetryDownload);
                }

                return _retryDownloadCommand;
            }
        }

        public ICommand CancelDownloadCommand
        {
            get
            {
                if (_cancelDownloadCommand == null)
                {
                    _cancelDownloadCommand = new DelegateCommand<string>(CancelDownload);
                }

                return _cancelDownloadCommand;
            }
        }


        public ICommand ShowLogCommand
        {
            get
            {
                if (_showLogCommand == null)
                {
                    _showLogCommand = new DelegateCommand<string>(ShowLog);
                }

                return _showLogCommand;
            }
        }

        public ICommand OpenDownloadFolderCommand
        {
            get
            {
                if (_openDownloadFolderCommand == null)
                {
                    _openDownloadFolderCommand = new DelegateCommand<string>(OpenDownloadFolder);
                }

                return _openDownloadFolderCommand;
            }
        }

        #endregion Properties

        #region Methods

        private void RetryDownload(string id)
        {
            try
            {
                lock (_commandLockObject)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        _downloadService.Retry(id);
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        private void CancelDownload(string id)
        {
            try
            {
                lock (_commandLockObject)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        if (Downloads.Single(d => d.Id == id).DownloadState == DownloadState.Downloading)
                        {
                            _downloadService.Cancel(id);
                            return;
                        }

                        _downloadService.Remove(id);
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        private void ShowLog(string id)
        {
            try
            {
                lock (_commandLockObject)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        VodDownload download = Downloads.Where(d => d.Id == id).FirstOrDefault();

                        if (download != null)
                        {
                            _navigationService.ShowLog(download);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        private void OpenDownloadFolder(string id)
        {
            try
            {
                lock (_commandLockObject)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        VodDownload download = Downloads.FirstOrDefault(d => d.Id == id);

                        if (download != null)
                        {
                            string folder = download.DownloadParams.Folder;

                            if (Directory.Exists(folder))
                            {
                                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                {
                                    using Process fileOpener = new Process();
                                    fileOpener.StartInfo.FileName = "explorer";
                                    fileOpener.StartInfo.Arguments = folder;
                                    fileOpener.Start();
                                    return;
                                }

                                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                                {
                                    OpenFolderLinux(folder, Path.Combine(folder, download.DownloadParams.Filename));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        /// <summary>
        /// Shows the file in the file manager (Dolphin on KDE Plasma) via the freedesktop FileManager1
        /// D-Bus interface, or opens the folder with xdg-open if that is not available
        /// </summary>
        private static void OpenFolderLinux(string folder, string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    ProcessStartInfo dbus = new ProcessStartInfo("dbus-send") { UseShellExecute = false };

                    foreach (string arg in new[] { "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                                 "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                                 "array:string:" + new Uri(file).AbsoluteUri, "string:" })
                    {
                        dbus.ArgumentList.Add(arg);
                    }

                    using Process p = Process.Start(dbus);

                    if (p != null && p.WaitForExit(5000) && p.ExitCode == 0)
                    {
                        return;
                    }
                }
            }
            catch
            {
                // Fall back to xdg-open
            }

            ProcessStartInfo xdg = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            xdg.ArgumentList.Add(folder);
            Process.Start(xdg)?.Dispose();
        }

        #endregion Methods

        #region EventHandlers

        private void DownloadService_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            FirePropertyChanged(e.PropertyName);
        }

        #endregion EventHandlers
    }
}