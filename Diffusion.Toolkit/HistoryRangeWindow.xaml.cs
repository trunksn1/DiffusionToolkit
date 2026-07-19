using System;
using System.Windows;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    public partial class HistoryRangeWindow : Window
    {
        /// <summary>The chosen start date (clamped to CivitAI's founding).</summary>
        public DateTime FromDate { get; private set; }

        public HistoryRangeWindow()
        {
            InitializeComponent();

            var founding = CivitaiPostsService.CivitaiFounding;
            FromPart.DisplayDateStart = founding;
            FromPart.DisplayDateEnd = DateTime.Today;
            // Default to one year back, but never before the founding date.
            var oneYearBack = DateTime.Today.AddYears(-1);
            FromPart.SelectedDate = oneYearBack < founding ? founding : oneYearBack;
        }

        private void Recover_Click(object sender, RoutedEventArgs e)
        {
            var founding = CivitaiPostsService.CivitaiFounding;
            var chosen = FromPart.SelectedDate ?? founding;
            if (chosen < founding) chosen = founding;
            FromDate = chosen.Date;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
