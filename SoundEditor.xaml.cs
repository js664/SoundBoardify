using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace VRSoundboard;

public partial class SoundEditor : Window
{
    private readonly AppCoordinator _core; private readonly Sound _sound;
    public SoundEditor(AppCoordinator core, Sound sound)
    {
        InitializeComponent(); _core = core; _sound = sound;
        NameBox.Text = sound.Name; LabelBox.Text = sound.ButtonLabel; IconBox.Text = sound.Icon;
        foreach (ComboBoxItem item in ModeBox.Items) if (item.Content?.ToString() == sound.Mode) ModeBox.SelectedItem = item;
        VolumeSlider.Value = sound.OutputGain;
        StartBox.Text = sound.StartSeconds.ToString("F2", CultureInfo.CurrentCulture);
        EndBox.Text = sound.EndSeconds?.ToString("F2", CultureInfo.CurrentCulture) ?? "";
        DurationText.Text = $"Source length: {sound.SourceDurationSeconds:F2} seconds";
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(StartBox.Text, CultureInfo.CurrentCulture, out var start) || start < 0) { MessageBox.Show("Enter a valid start time."); return; }
        double? end = null;
        if (!string.IsNullOrWhiteSpace(EndBox.Text)) { if (!double.TryParse(EndBox.Text, CultureInfo.CurrentCulture, out var value) || value <= start) { MessageBox.Show("End must be after start."); return; } end = value; }
        try
        {
            _core.UpdateSound(_sound.Id, s => { s.Name = NameBox.Text; s.ButtonLabel = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(); s.Icon = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text.Trim(); s.Mode = ((ComboBoxItem)ModeBox.SelectedItem).Content.ToString()!; s.OutputGain = (float)VolumeSlider.Value; s.StartSeconds = start; s.EndSeconds = end; });
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Could not save sound"); }
    }
}
