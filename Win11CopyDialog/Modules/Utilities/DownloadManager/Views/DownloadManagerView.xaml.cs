using System.Windows.Controls;
using Win11CopyDialog.Modules.Utilities.DownloadManager.ViewModels;

namespace Win11CopyDialog.Modules.Utilities.DownloadManager.Views
{
    public partial class DownloadManagerView : UserControl
    {
        public DownloadManagerView()
        {
            InitializeComponent();
            DataContext = new DownloadManagerViewModel();
        }
    }
}
