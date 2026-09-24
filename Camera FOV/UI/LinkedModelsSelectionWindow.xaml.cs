using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

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
            ThemeManager.Register(this);
            Loaded += (s, e) => Motion.Reveal(ContentRoot);

            _allLinks = linkNames.Select(name => new LinkViewModel { Name = name, IsSelected = true }).ToList();
            LinksList.ItemsSource = _allLinks;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
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
            if (LinksPanel.Visibility == Visibility.Collapsed)
            {
                // Show the list in place; confirming it becomes the primary action
                LinksPanel.Visibility = Visibility.Visible;
                Motion.Reveal(LinksPanel);
                BtnYes.Visibility = Visibility.Collapsed; // Hide "Include all" to avoid confusion
                BtnSelect.Content = "Trace selected";
                BtnSelect.Style = (Style)FindResource("Button.Primary");
                BtnSelect.IsDefault = true;
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
