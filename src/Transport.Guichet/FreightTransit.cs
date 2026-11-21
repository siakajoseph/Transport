using System.Net.Http.Json;
using System.Windows;

namespace Transport.Guichet;

public partial class MainWindow
{
    public record FreightStop(int TripId,int Stop,string Name,DateTime Departure)
    {
        public string Description=>$"Voyage {TripId} · {Departure.ToUniversalTime():dd/MM HH:mm} · {Name}";
    }
    private record ParcelState(string State,long Due,string Recipient);

    private async void FreightTransitOpened(object sender,RoutedEventArgs e)=>await Run(async ()=>{
        TransitStop.ItemsSource=await api.GetFromJsonAsync<FreightStop[]>("api/fret/arrets");
        TransitStop.SelectedIndex=-1;
        TransitStatus.Text="Sélectionner le voyage et l’arrêt physique de contrôle.";
    });

    private async Task<ParcelState?> InspectParcel(Guid id)
    {
        TransitStatus.Text="";
        using var response=await api.GetAsync($"api/fret/dossiers/{id}/etat");
        if(!response.IsSuccessStatusCode){TransitStatus.Text=await ErrorText(response);return null;}
        var state=await response.Content.ReadFromJsonAsync<ParcelState>();
        if(state is not null) TransitStatus.Text=$"{state.State} · Destinataire : {state.Recipient} · Reste dû : {state.Due:N0} FCFA.";
        return state;
    }

    private bool ParcelReference(out Guid id)
    {
        if(Guid.TryParse(TransitRef.Text.Trim(),out id)) return true;
        TransitStatus.Text="Référence de colis invalide.";
        return false;
    }

    private async void InspectTransit(object sender,RoutedEventArgs e)=>await Run(async ()=>{
        if(ParcelReference(out var id)) await InspectParcel(id);
    });
    private async void LoadParcel(object sender,RoutedEventArgs e)=>await ChangeParcel("charger");
    private async void ArriveParcel(object sender,RoutedEventArgs e)=>await ChangeParcel("arriver");

    private Task ChangeParcel(string action)=>Run(async ()=>{
        if(!ParcelReference(out var id)) return;
        if(TransitStop.SelectedItem is not FreightStop point){TransitStatus.Text="Sélectionner le voyage et l’arrêt.";return;}
        using var reply=await api.PostAsJsonAsync($"api/fret/dossiers/{id}/mouvements/{action}",new {point.TripId,point.Stop,agent=Agent});
        if(!reply.IsSuccessStatusCode){TransitStatus.Text=await ErrorText(reply);return;}
        await InspectParcel(id);
    });

    private async void HandOverParcel(object sender,RoutedEventArgs e)=>await Run(async ()=>{
        if(!ParcelReference(out var id)) return;
        if(TransitStop.SelectedItem is not FreightStop point){TransitStatus.Text="Sélectionner la destination.";return;}
        var state=await InspectParcel(id);
        if(state?.State!="En attente de retrait") return;
        await RefreshShift();
        if(shift is null){TransitStatus.Text="Ouvrir une caisse.";return;}
        if(MessageBox.Show($"La pièce présentée correspond-elle à {state.Recipient} ? Confirmer {state.Due:N0} FCFA reçus et attendre la validation avant de remettre le colis.",
            "Retrait en agence",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        TransitStatus.Text="Confirmation en cours. En cas de coupure, consulter l’état avant de reprendre les espèces.";
        using var reply=await api.PostAsJsonAsync($"api/fret/dossiers/{id}/retrait",new {point.TripId,point.Stop,
            session=shift,code=PickupCode.Password.Trim(),idSuffix=PickupDocument.Text.Trim(),amount=state.Due});
        if(!reply.IsSuccessStatusCode){TransitStatus.Text=await ErrorText(reply);return;}
        PickupCode.Clear();
        PickupDocument.Clear();
        await InspectParcel(id);
    });
}
