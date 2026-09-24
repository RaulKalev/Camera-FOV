using MaterialDesignThemes.Wpf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Camera_FOV.UI
{
    // Themed replacement for MessageBox / TaskDialog: a heading, a short explanation, an optional
    // list of items (each naming a field or result and what it means) and optional technical details.
    public partial class MessageDialog : Window
    {
        public enum Kind
        {
            Info,
            Success,
            Warning,
            Error
        }

        public class Item
        {
            public string Label { get; set; }
            public string Detail { get; set; }

            public Item(string label, string detail)
            {
                Label = label;
                Detail = detail;
            }
        }

        // The plugin window dialogs centre on when no owner is given (set by MainWindow).
        public static Window DefaultOwner { get; set; }

        private MessageDialog(Kind kind, string heading, string message, IList<Item> items, string details)
        {
            InitializeComponent();
            ThemeManager.Register(this);
            Loaded += (s, e) => Motion.Reveal(ContentRoot);

            ApplyKind(kind);

            HeadingText.Text = heading;
            MessageText.Text = message;
            MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;

            if (items != null && items.Any())
            {
                ItemsList.ItemsSource = items;
                ItemsPanel.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(details))
            {
                DetailsText.Text = details;
                DetailsButton.Visibility = Visibility.Visible;
            }
        }

        private void ApplyKind(Kind kind)
        {
            string tile, glyph;
            switch (kind)
            {
                case Kind.Success:
                    tile = "Status.SuccessSubtle"; glyph = "Status.Success"; KindIcon.Kind = PackIconKind.CheckCircleOutline;
                    break;
                case Kind.Warning:
                    tile = "Status.WarningSubtle"; glyph = "Status.Warning"; KindIcon.Kind = PackIconKind.AlertOutline;
                    break;
                case Kind.Error:
                    tile = "Status.ErrorSubtle"; glyph = "Status.Error"; KindIcon.Kind = PackIconKind.AlertCircleOutline;
                    break;
                default:
                    tile = "Accent.Subtle"; glyph = "Accent.Text"; KindIcon.Kind = PackIconKind.InformationOutline;
                    break;
            }

            // Resource references keep the colours live when the theme is switched
            KindTile.SetResourceReference(BackgroundProperty, tile);
            KindIcon.SetResourceReference(ForegroundProperty, glyph);
        }

        public static void Show(Kind kind, string heading, string message = null, IList<Item> items = null, string details = null, Window owner = null)
        {
            var dialog = new MessageDialog(kind, heading, message, items, details);

            owner = owner ?? DefaultOwner;
            if (owner != null && owner.IsVisible)
                dialog.Owner = owner;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            dialog.ShowDialog();
        }

        public static void ShowInfo(string heading, string message = null, IList<Item> items = null, Window owner = null)
            => Show(Kind.Info, heading, message, items, null, owner);

        public static void ShowSuccess(string heading, string message = null, IList<Item> items = null, Window owner = null)
            => Show(Kind.Success, heading, message, items, null, owner);

        public static void ShowWarning(string heading, string message = null, IList<Item> items = null, Window owner = null)
            => Show(Kind.Warning, heading, message, items, null, owner);

        /// <summary>
        /// Asks a question with a button per choice: the first is the primary one, the last also closes
        /// the dialog on Esc. Returns the index of the choice, or the last one when the dialog is closed.
        /// </summary>
        public static int Ask(Kind kind, string heading, string message, string[] choices, Window owner = null)
        {
            var dialog = new MessageDialog(kind, heading, message, null, null);
            dialog.OkButton.Visibility = Visibility.Collapsed;

            int answer = choices.Length - 1;
            for (int i = 0; i < choices.Length; i++) // Primary ends up on the right, like the other footers
            {
                int index = i;
                var button = new System.Windows.Controls.Button
                {
                    Content = choices[i],
                    MinWidth = 72,
                    Margin = new Thickness(8, 0, 0, 0),
                    IsDefault = i == 0,
                    IsCancel = i == choices.Length - 1,
                    Style = (Style)dialog.FindResource(i == 0 ? "Button.Primary" : "Button.Secondary")
                };
                button.Click += (s, e) => { answer = index; dialog.Close(); };
                dialog.ChoicesPanel.Children.Insert(0, button);
            }

            owner = owner ?? DefaultOwner;
            if (owner != null && owner.IsVisible)
                dialog.Owner = owner;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            dialog.ShowDialog();
            return answer;
        }

        // For unexpected failures: a plain explanation up front, the exception behind "Show details".
        public static void ShowError(string heading, string message, Exception ex = null, Window owner = null)
            => Show(Kind.Error, heading, message, null, ex?.ToString(), owner);

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void DetailsButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = DetailsPanel.Visibility != Visibility.Visible;
            DetailsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DetailsButton.Content = show ? "Hide details" : "Show details";
            if (show) Motion.Reveal(DetailsPanel);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
