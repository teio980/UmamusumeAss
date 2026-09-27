using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using UmamusumeWpfGui.ViewModels.Tasks;
using UmamusumeWpfGui.Views.Tasks;

namespace UmamusumeWpfGui.Tests.Views;

public sealed class NormalCareerSkillSettingsViewBindingTests
{
    [Fact]
    public void Skill_picker_bindings_survive_search_and_target_changes()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var model = new NormalCareerSkillSettingsViewModel();
                var view = new NormalCareerSkillSettingsView { DataContext = model };
                view.Measure(new Size(900, 900));
                FlushBindings();

                var first = model.FilteredSkills[0];
                model.SearchText = first.SkillId.ToString(CultureInfo.InvariantCulture);
                model.SelectedAvailableSkill = first;
                model.AddSelectedSkillCommand.Execute(null);
                model.SelectedTargetSkill = first;
                model.SearchText = string.Empty;
                model.RemoveSelectedSkillCommand.Execute(null);
                FlushBindings();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF binding thread timed out.");
        Assert.Null(error);
    }

    private static void FlushBindings()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
