using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;

namespace Transport.Guichet;

public partial class MainWindow
{
    private record FreightFile(int TripId,int Origin,int Destination,string Leg,int Grams,int LengthCm,int WidthCm,
        int HeightCm,long Xof,DateTime? DepositedAt);
    private record FreightPrice(int Kg,long Xof);
    private FreightFile? freightFile;

    private void FreightReferenceChanged(object sender,TextChangedEventArgs e)
    {
        freightFile=null;
        if(FreightForm is null) return;
        FreightForm.IsEnabled=false;
        FreightStatus.Text="";
        SenderName.Clear(); SenderPhone.Clear(); ReceiverName.Clear(); ReceiverPhone.Clear();
        FreightNature.SelectedIndex=0; FreightPayment.SelectedIndex=0;
    }

    private async Task LoadFreight(Guid id)
    {
        FreightForm.IsEnabled=false;
        using var reply=await api.GetAsync($"api/fret/dossiers/{id}");
        if(!reply.IsSuccessStatusCode) { FreightStatus.Text=await ErrorText(reply); return; }
        freightFile=await reply.Content.ReadFromJsonAsync<FreightFile>();
        if(freightFile is null) return;
        FreightG.Text=freightFile.Grams.ToString();
        FreightL.Text=freightFile.LengthCm.ToString();
        FreightW.Text=freightFile.WidthCm.ToString();
        FreightH.Text=freightFile.HeightCm.ToString();
        FreightForm.IsEnabled=freightFile.DepositedAt is null;
        FreightStatus.Text=freightFile.DepositedAt is null
            ? $"{freightFile.Leg} · estimation {freightFile.Xof:N0} FCFA. Vérifier les mesures en agence."
            : $"{freightFile.Leg} · déposé le {freightFile.DepositedAt:dd/MM/yyyy HH:mm} UTC · {freightFile.Xof:N0} FCFA.";
    }

    private async void FindFreight(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        if(Guid.TryParse(FreightRef.Text.Trim(),out var id)) await LoadFreight(id);
        else FreightStatus.Text="Référence de colis invalide.";
    });

    private async void ReceiveFreight(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        var file=freightFile;
        if(file is null || !Guid.TryParse(FreightRef.Text.Trim(),out var id)) return;
        if(!int.TryParse(FreightG.Text,out var grams) || !int.TryParse(FreightL.Text,out var lengthCm) ||
           !int.TryParse(FreightW.Text,out var widthCm) || !int.TryParse(FreightH.Text,out var heightCm))
        { FreightStatus.Text="Saisir les valeurs mesurées en grammes et centimètres."; return; }
        await RefreshShift();
        if(shift is null) { FreightStatus.Text="Ouvrir une caisse."; return; }
        string payment=((ComboBoxItem)FreightPayment.SelectedItem).Content.ToString()!;
        string nature=((ComboBoxItem)FreightNature.SelectedItem).Content.ToString()!;
        var measurements=new {file.TripId,file.Origin,file.Destination,grams,lengthCm,widthCm,heightCm};
        using var estimate=await api.PostAsJsonAsync("api/fret/devis",measurements);
        if(!estimate.IsSuccessStatusCode) { FreightStatus.Text=await ErrorText(estimate); return; }
        var quote=await estimate.Content.ReadFromJsonAsync<FreightPrice>();
        if(quote is null) return;
        var instruction=payment=="payé"?"Confirmer la réception des espèces et du colis.":"Confirmer le dépôt avec paiement au retrait.";
        if(MessageBox.Show($"{file.Leg} · {quote.Kg} kg facturables · {quote.Xof:N0} FCFA.\n{instruction}",
            "Réception en agence",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        var receipt=new {session=shift,grams,lengthCm,widthCm,heightCm,sender=SenderName.Text.Trim(),senderPhone=SenderPhone.Text.Trim(),
            recipient=ReceiverName.Text.Trim(),recipientPhone=ReceiverPhone.Text.Trim(),payment,nature,agreedXof=quote.Xof};
        FreightForm.IsEnabled=false;
        FreightStatus.Text="En cas de coupure, retrouver ce dossier avant de reprendre les espèces.";
        using var reply=await api.PostAsJsonAsync($"api/fret/dossiers/{id}/depot",receipt);
        if(!reply.IsSuccessStatusCode) { FreightStatus.Text=await ErrorText(reply); FreightForm.IsEnabled=true; return; }
        await LoadFreight(id);
    });
}
