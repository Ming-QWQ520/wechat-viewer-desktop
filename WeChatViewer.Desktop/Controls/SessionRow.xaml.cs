using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WeChatViewer.Desktop.Controls;
using WeChatViewer.Desktop.ViewModels;

namespace WeChatViewer.Desktop.Controls;

/// <summary>Session list item renderer.</summary>
public sealed partial class SessionRow : UserControl
{
    private SessionViewModel _vm;

    public SessionRow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as SessionViewModel;
        Build();
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.TimeText))
        {
            TimeText.Text = _vm?.TimeText ?? "";
        }
    }

    private void Build()
    {
        if (_vm == null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        NameText.Text = _vm.Name;
        GroupTag.Text = _vm.GroupTag;
        GroupTag.Visibility = _vm.IsGroup ? Visibility.Visible : Visibility.Collapsed;
        TimeText.Text = _vm.TimeText;
        SummaryText.Text = _vm.Summary;
        if (_vm.HasUnread)
        {
            UnreadBadge.Visibility = Visibility.Visible;
            UnreadText.Text = _vm.Unread > 99 ? "99+" : _vm.Unread.ToString();
        }
        else
        {
            UnreadBadge.Visibility = Visibility.Collapsed;
        }

        AvatarBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(UiHelpers.AvatarColor(_vm.Name));
        AvatarImage.Source = null;
        AvatarInitial.Text = UiHelpers.Initial(_vm.Name);
        AvatarInitial.Visibility = Visibility.Visible;
        AvatarImage.Visibility = Visibility.Collapsed;
        _ = LoadAvatarAsync(_vm.Username);
    }

    private async System.Threading.Tasks.Task LoadAvatarAsync(string username)
    {
        var resolver = MessageInteractions.AvatarResolver;
        if (resolver == null || string.IsNullOrEmpty(username)) return;
        try
        {
            var source = await resolver(username);
            if (_vm == null || _vm.Username != username || source == null) return;
            AvatarImage.Source = source;
            AvatarImage.Visibility = Visibility.Visible;
            AvatarInitial.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // keep the colored initial fallback
        }
    }
}
