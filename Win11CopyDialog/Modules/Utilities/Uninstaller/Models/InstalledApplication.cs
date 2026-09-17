using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Win11CopyDialog.Modules.Utilities.Uninstaller.Models
{
    public class InstalledApplication : INotifyPropertyChanged
    {
        public string DisplayName { get; set; } = string.Empty;
        public string DisplayVersion { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string InstallDate { get; set; } = string.Empty;
        public string InstallLocation { get; set; } = string.Empty;
        public string UninstallString { get; set; } = string.Empty;
        public string RegistryKeyPath { get; set; } = string.Empty;
        public string EstimatedSize { get; set; } = string.Empty;
        public string DisplayIcon { get; set; } = string.Empty;
        
        public bool IsSystemComponent { get; set; }
        public bool IsAppxPackage { get; set; }
        
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
