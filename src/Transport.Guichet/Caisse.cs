using System.IO;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Transport.Guichet;

public partial class MainWindow
{
    private Guid? shift;
    private string Agent => profile?.Label ?? (Environment.UserDomainName + "/" + Environment.UserName);
    private record Shift(Guid? Id);
    private record Balance(long Expected, long Counted, long Difference);
    private record Ticket(string Passenger, int Number, string Label, int Xof, DateTime PaidAt, string Token);

    private async Task RefreshShift()
    {
        var data = await api.GetFromJsonAsync<Shift>("api/caisse?agent=" + Uri.EscapeDataString(Agent));
        shift = data?.Id;
        ShiftStatus.Text = Agent + (shift.HasValue ? " · caisse ouverte" : " · caisse fermée");
    }

    private async void OpenClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!int.TryParse(Amount.Text,out var opening) || opening < 0) { Status.Text="Fond invalide."; return; }
        using var reply=await api.PostAsJsonAsync("api/caisse",new { agent=Agent,opening });
        if (!reply.IsSuccessStatusCode) { Status.Text=await ErrorText(reply); return; }
        await RefreshShift();
        Status.Text="Caisse ouverte.";
    });

    private async void CloseClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (pending is not null) { Status.Text="Terminer l’option en cours avant la clôture."; return; }
        if (!shift.HasValue) { await RefreshShift(); if (!shift.HasValue) { Status.Text="Caisse fermée."; return; } }
        if (!int.TryParse(Amount.Text,out var counted) || counted < 0) { Status.Text="Comptage invalide."; return; }
        if (MessageBox.Show($"Clôturer avec {counted:N0} FCFA comptés ?", "Clôture",MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return;
        using var reply=await api.PostAsJsonAsync($"api/caisse/{shift}/cloture",new { counted });
        if (!reply.IsSuccessStatusCode) { Status.Text=await ErrorText(reply); return; }
        var balance=await reply.Content.ReadFromJsonAsync<Balance>() ?? throw new InvalidDataException();
        Status.Text=$"Attendu {balance.Expected:N0} · Compté {balance.Counted:N0} · Écart {balance.Difference:N0} FCFA.";
        await RefreshShift();
    });

    private async void PrintClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!Guid.TryParse(TicketRef.Text,out var id)) { Status.Text="Référence de billet invalide."; return; }
        var document=await api.GetFromJsonAsync<Ticket>($"api/billets/{id}") ?? throw new InvalidDataException();
        var dialog=new PrintDialog();
        if (dialog.ShowDialog()!=true) return;
        var bitmap=QrBitmap(document.Token);
        var paper=new StackPanel { Width=Math.Min(276,dialog.PrintableAreaWidth), Background=Brushes.White, Margin=new Thickness(4) };
        foreach (var line in new[] {profile?.Company ?? "TRANSPORT · DÉMONSTRATION",document.Label,document.Passenger,$"Siège {document.Number} · {document.Xof:N0} FCFA",
            document.PaidAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),id.ToString()})
            paper.Children.Add(new TextBlock { Text=line, TextWrapping=TextWrapping.Wrap, FontSize=13 });
        paper.Children.Add(new Image { Source=bitmap, Width=142,Height=142 });
        paper.Measure(new Size(dialog.PrintableAreaWidth,double.PositiveInfinity));
        paper.Arrange(new Rect(paper.DesiredSize));
        try { dialog.PrintVisual(paper,"Billet démo "+id); Status.Text="Billet transmis à l’imprimante."; }
        catch (System.Printing.PrintSystemException ex) { Status.Text="Vente conservée. Impression à reprendre. "+ex.Message; }
    });
    private static BitmapImage QrBitmap(string token)
    {
        using var data=QRCodeGenerator.GenerateQrCode(token,QRCodeGenerator.ECCLevel.M);
        using var qr=new PngByteQRCode(data);
        using var stream=new MemoryStream(qr.GetGraphic(6));
        var bitmap=new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption=BitmapCacheOption.OnLoad;
        bitmap.StreamSource=stream;
        bitmap.EndInit();
        return bitmap;
    }
}
