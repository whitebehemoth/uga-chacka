using System;
using System.Windows;

namespace uga_chacka
{
    public partial class FindReplaceWindow : Window
    {
        public event EventHandler? FindNextRequested;
        public event EventHandler? ReplaceRequested;
        public event EventHandler? ReplaceAllRequested;

        public FindReplaceWindow()
        {
            InitializeComponent();
        }

        public string FindText => FindTextBox.Text;
        public string ReplaceText => ReplaceTextBox.Text;
        public bool UseRegex => RegexCheckBox.IsChecked == true;

        public void SetReplaceMode(bool enabled)
        {
            ReplaceLabel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            ReplaceTextBox.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            ReplaceButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            ReplaceAllButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) => FindNextRequested?.Invoke(this, EventArgs.Empty);

        private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceRequested?.Invoke(this, EventArgs.Empty);

        private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAllRequested?.Invoke(this, EventArgs.Empty);

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
