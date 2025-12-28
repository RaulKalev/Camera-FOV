using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Camera_FOV.UI
{
    public partial class LinkedModelsSelectionWindow : Window
    {
        public enum SelectionResult
        {
            None,
            All,
            Selected
        }

        public SelectionResult Result { get; private set; } = SelectionResult.None;
        public List<LinkViewModel> SelectedLinks { get; private set; } = new List<LinkViewModel>();

        private List<LinkViewModel> _allLinks;

        public LinkedModelsSelectionWindow(List<string> linkNames)
        {
            InitializeComponent();

            _allLinks = linkNames.Select(name => new LinkViewModel { Name = name, IsSelected = true }).ToList();
            LinksDataGrid.ItemsSource = _allLinks;
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            Result = SelectionResult.None;
            Close();
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            Result = SelectionResult.All;
            Close();
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            if (LinksDataGrid.Visibility == Visibility.Collapsed)
            {
                // Show list for manual selection
                LinksDataGrid.Visibility = Visibility.Visible;
                BtnYes.Visibility = Visibility.Collapsed; // Hide "Yes (All)" to avoid confusion
                BtnSelect.Content = "Confirm Selection";
            }
            else
            {
                // Confirm selection
                SelectedLinks = _allLinks.Where(l => l.IsSelected).ToList();
                Result = SelectionResult.Selected;
                Close();
            }
        }
    }

    public class LinkViewModel
    {
        public string Name { get; set; }
        public bool IsSelected { get; set; }
    }
}
