using System.Globalization;
using System.Net.Http.Json;
using System.Windows;

namespace Transport.Guichet;

public partial class MainWindow
{
    public record TripTemplate(int Id,string Label);
    private record Scheduled(int Id);

    private async void ScheduleOpened(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        Templates.ItemsSource=await api.GetFromJsonAsync<TripTemplate[]>("api/programmation/modeles");
        Templates.SelectedIndex=Templates.Items.Count>0 ? 0 : -1;
        DepartureDate.SelectedDate ??= DateTime.UtcNow.Date.AddDays(1);
    });

    private async void ScheduleClicked(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        if (Templates.SelectedItem is not TripTemplate model || DepartureDate.SelectedDate is not DateTime date ||
            !TimeOnly.TryParseExact(DepartureTime.Text,"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time) ||
            !int.TryParse(CoachNumber.Text,out var coachNo) || coachNo is <1 or >99)
        {
            ScheduleResult.Text="Sélectionner le modèle, la date, une heure au format HH:mm et le numéro de car (1 à 99).";
            return;
        }
        var departureAt=new DateTimeOffset(DateTime.SpecifyKind(date.Date+time.ToTimeSpan(),DateTimeKind.Utc));
        using var response=await api.PostAsJsonAsync("api/programmation",new {templateId=model.Id,departureAt,coachNo});
        if (!response.IsSuccessStatusCode) { ScheduleResult.Text=await ErrorText(response); return; }
        var trip=await response.Content.ReadFromJsonAsync<Scheduled>();
        ScheduleResult.Text=$"Voyage {trip!.Id} programmé · {departureAt:dd/MM/yyyy HH:mm} à Abidjan · car {coachNo}.";
        if (pending is null)
        {
            await LoadRoutes();
            Routes.SelectedItem=Routes.Items.OfType<Route>().FirstOrDefault(r=>r.TripId==trip.Id);
            await LoadSeats();
        }
        Stops.ItemsSource=await api.GetFromJsonAsync<StopChoice[]>("api/embarquement/arrets");
        Stops.SelectedItem=Stops.Items.OfType<StopChoice>().FirstOrDefault(s=>s.TripId==trip.Id);
        await ReadManifest();
    });
}
