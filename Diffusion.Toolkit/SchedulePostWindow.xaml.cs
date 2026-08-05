using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace Diffusion.Toolkit
{
    public partial class SchedulePostWindow : Window
    {
        public DateTime PublishAt { get; private set; }
        public string? PostTitle { get; private set; }

        public SchedulePostWindow(string filePath, DateTime targetDate)
        {
            InitializeComponent();

            FileNameText.Text = Path.GetFileName(filePath);

            for (int h = 0; h < 24; h++) HourPart.Items.Add(h.ToString("D2"));
            foreach (var m in new[] { 0, 15, 30, 45 }) MinutePart.Items.Add(m.ToString("D2"));

            // Default to the dropped-on day at the next full hour (or noon if today
            // is already late). CivitAI wants at least an hour's notice, so on
            // today that means two hours out, not one.
            var now = DateTime.Now;
            var date = targetDate.Date;
            int hour = date == now.Date ? Math.Min(now.Hour + 2, 23) : 12;

            DatePart.SelectedDate = date;
            DatePart.DisplayDateStart = now.Date;
            // CivitAI rejects publish dates further out than this, so stop the
            // picker there rather than let the upload fail against the ceiling.
            DatePart.DisplayDateEnd = Services.CivitaiPostsService.LastSchedulableDate;
            HourPart.SelectedIndex = hour;
            MinutePart.SelectedIndex = 0;

            LimitText.Text = Services.CivitaiPostsService.ScheduleLimitMessage;
        }

        private void Schedule_Click(object sender, RoutedEventArgs e)
        {
            if (DatePart.SelectedDate == null)
            {
                MessageBox.Show(this, "Please choose a date.", "Schedule", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var date = DatePart.SelectedDate.Value.Date;
            int hour = HourPart.SelectedIndex >= 0 ? HourPart.SelectedIndex : 12;
            int minute = MinutePart.SelectedItem is string ms && int.TryParse(ms, out var m) ? m : 0;

            var publishAt = date.AddHours(hour).AddMinutes(minute);
            if (publishAt < Services.CivitaiPostsService.EarliestSchedulableTime)
            {
                MessageBox.Show(this, Services.CivitaiPostsService.ScheduleFloorMessage, "Schedule",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (date > Services.CivitaiPostsService.LastSchedulableDate)
            {
                MessageBox.Show(this, Services.CivitaiPostsService.ScheduleLimitMessage,
                    "Schedule", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PublishAt = publishAt;
            PostTitle = string.IsNullOrWhiteSpace(TitlePart.Text) ? null : TitlePart.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
