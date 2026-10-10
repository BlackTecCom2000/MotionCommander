using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Models
{
    public class InstalledApplication : INotifyPropertyChanged
    {
        public string DisplayName { get; set; } = string.Empty;
        public string DisplayVersion { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string InstallDate { get; set; } = string.Empty;
        public DateTime? InstallDateParsed { get; set; }
        public string InstallDateFormatted => InstallDateParsed.HasValue 
            ? InstallDateParsed.Value.ToString("dd.MM.yyyy") 
            : (!string.IsNullOrWhiteSpace(InstallDate) ? InstallDate : "—");

        public string InstallLocation { get; set; } = string.Empty;
        public bool InstallLocationExists { get; set; }

        public string UninstallString { get; set; } = string.Empty;
        public string QuietUninstallString { get; set; } = string.Empty;
        public string ModifyPath { get; set; } = string.Empty;
        public string HelpLink { get; set; } = string.Empty;
        public string RegistryKeyPath { get; set; } = string.Empty;

        public long SizeBytes { get; set; } = -1;
        public string EstimatedSize { get; set; } = string.Empty;
        public string DisplayIcon { get; set; } = string.Empty;

        public bool IsSystemComponent { get; set; }
        public bool IsAppxPackage { get; set; }
        public bool IsOrphaned { get; set; }
        public string Architecture { get; set; } = "64-bit";
        public bool IsMsi { get; set; }

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (_icon != value)
                {
                    _icon = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string TypeBadge
        {
            get
            {
                if (IsSystemComponent) return "Системный";
                if (IsOrphaned) return "Остаток";
                if (IsMsi) return "MSI";
                return Architecture;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
