using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using DeathNote.Wpf.Data;
using DeathNote.Wpf.Infrastructure;
using DeathNote.Wpf.Models;

namespace DeathNote.Wpf.Views;

/// <summary>Persistent inline editor, hosted once by MainWindow, without a separate HWND.</summary>
public partial class EditMatchPanel : UserControl
{
    private MatchRecord? _match;
    private ComboBoxItem? _customResult;
    private bool _reviewMode;
    public EditMatchPanel()
    {
        InitializeComponent();
    }
    public void HideEditor()
    {
        BeginAnimation(OpacityProperty,null);
        Visibility=Visibility.Collapsed;
    }
    public void Open(MatchRecord match, bool forReview = false)
    {
        _reviewMode=false;
        _match=match;
        TitleInput.Text=match.Title;
        MapInput.Text=match.MapEnglish;
        StartedInput.Text=DisplayTime(match.Started);
        LastAtInput.Text=DisplayTime(match.LastAt);
        StartTurnInput.Text=match.StartTurn?.ToString(CultureInfo.InvariantCulture)??"";
        LastTurnInput.Text=match.LastTurn?.ToString(CultureInfo.InvariantCulture)??"";
        NoteInput.Text=match.Note;
        FavoriteToggle.IsChecked=match.Favorite;
        // Preserve unusual legacy result codes instead of coercing them to defaults.
        if(_customResult is not null)
        {
            ResultSelect.Items.Remove(_customResult);
            _customResult=null;
        }
        ResultSelect.SelectedValue=match.Result;
        if(ResultSelect.SelectedIndex<0)
        {
            var item=new ComboBoxItem{Content=match.Result,Tag=match.Result};
            ResultSelect.Items.Add(item);ResultSelect.SelectedItem=item;
            _customResult=item;
        }
        var options=new List<WinnerOption>{new("__unset__","Not selected"),new("","No winner"),
            new("__local__",string.IsNullOrEmpty(match.LocalNick)?"You":match.LocalNick+" (you)")};
        foreach(var p in match.PlayerDetails)options.Add(new WinnerOption(p.SteamId,p.Nickname));
        WinnerSelect.ItemsSource=options;
        WinnerSelect.SelectedValue=match.WinnerId;
        if(WinnerSelect.SelectedIndex<0)WinnerSelect.SelectedIndex=0;
        BeginAnimation(OpacityProperty,null);
        Opacity=0;
        Visibility=Visibility.Visible;
        BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(145))
        {EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}});
        _reviewMode=forReview;
        if (forReview)
        {
            ResultSelect.Focus();
            ResultSelect.IsDropDownOpen=true;
        }
        else TitleInput.Focus();
    }
    private void ResultChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reviewMode && IsVisible && ResultSelect.SelectedItem is not null)
            WinnerSelect.Focus();
    }
    private void Cancel(object sender,RoutedEventArgs e)=>HideEditor();
    private static string DisplayTime(string iso) => DateTimeOffset.TryParse(iso,out var stamp)
        ? stamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss",CultureInfo.InvariantCulture) : iso;

    private static string? ParseTime(string raw,string old)
    {
        raw=raw.Trim();
        if(raw==DisplayTime(old))return old; // Preserve timestamp precision and original offset.
        if(DateTime.TryParseExact(raw,["dd.MM.yyyy HH:mm:ss","dd.MM.yyyy HH:mm"],
            CultureInfo.InvariantCulture,DateTimeStyles.None,out var local))
            return new DateTimeOffset(DateTime.SpecifyKind(local,DateTimeKind.Local)).ToString("O");
        if(DateTimeOffset.TryParse(raw,CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))
            return parsed.ToString("O");
        return null;
    }
    private static bool TryTurn(string text,out long? result)
    {
        result=null;
        if(string.IsNullOrWhiteSpace(text))return true;
        if(long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out long value) && value>=0)
        {result=value;return true;}
        return false;
    }
    private async void Save(object sender,RoutedEventArgs e)
    {
        if(_match is null)return;
        string result=ResultSelect.SelectedValue as string??"Не указано";
        string winner=WinnerSelect.SelectedValue as string??"__unset__";
        string? start=ParseTime(StartedInput.Text,_match.Started);
        string? last=ParseTime(LastAtInput.Text,_match.LastAt);
        if(start is null || last is null || !TryTurn(StartTurnInput.Text,out var startTurn) ||
           !TryTurn(LastTurnInput.Text,out var lastTurn))
        {
            MessageBox.Show("Check dates (dd.MM.yyyy HH:mm:ss) and turn numbers.",
                "Death Note",MessageBoxButton.OK,MessageBoxImage.Warning);
            return;
        }
        var patch=new MatchPatch(TitleInput.Text.Trim(),NoteInput.Text,result,winner,
            FavoriteToggle.IsChecked==true,false,_match.Deleted,
            UpdateMetadata:true,Map:MapInput.Text.Trim(),Started:start,LastAt:last,
            StartTurn:startTurn,LastTurn:lastTurn);
        if(DataContext is not MainViewModel vm)return;
        if(await vm.SaveMatchCheckedAsync(_match.Id,patch))HideEditor();
    }
}
