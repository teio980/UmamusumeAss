using System.Collections.Generic;
using System.Linq;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.ViewModels.Dialogs;

namespace UmamusumeWpfGui.Tests.ViewModels.Dialogs;

public sealed class SelectionDialogViewModelTests
{




    [Fact]
    public void Constructor_WithCandidates_PopulatesItems()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
            new("Nox", @"E:\Nox\nox_adb.exe"),
        };

        var vm = new SelectionDialogViewModel(candidates);

        Assert.Equal(3, vm.Items.Count);
        Assert.Equal("BlueStacks", vm.Items[0].EmulatorName);
        Assert.Equal(@"C:\BS\HD-Adb.exe", vm.Items[0].AdbPath);
    }

    [Fact]
    public void Constructor_WithSingleCandidate_PreselectsIt()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
        };

        var vm = new SelectionDialogViewModel(candidates);

        Assert.Single(vm.Items);
        Assert.True(vm.Items[0].IsSelected);
    }

    [Fact]
    public void Constructor_WithMultipleCandidates_NoPreselection()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };

        var vm = new SelectionDialogViewModel(candidates);

        Assert.All(vm.Items, item => Assert.False(item.IsSelected));
    }

    [Fact]
    public void Constructor_WithEmptyCandidates_StillSucceeds()
    {
        var vm = new SelectionDialogViewModel([]);

        Assert.Empty(vm.Items);
        Assert.Null(vm.SelectedCandidate);
    }

    [Fact]
    public void Constructor_WithNullAdbPath_IncludesItem()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("Unknown", null),
        };

        var vm = new SelectionDialogViewModel(candidates);

        Assert.Single(vm.Items);
        Assert.Null(vm.Items[0].AdbPath);
    }





    [Fact]
    public void SelectCandidate_SetsIsSelectedOnTargetAndClearsOthers()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };

        var vm = new SelectionDialogViewModel(candidates);

        vm.Items[1].IsSelected = true;

        Assert.False(vm.Items[0].IsSelected);
        Assert.True(vm.Items[1].IsSelected);
    }

    [Fact]
    public void SelectCandidate_UpdatesSelectedCandidate()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };

        var vm = new SelectionDialogViewModel(candidates);

        vm.Items[1].IsSelected = true;

        Assert.NotNull(vm.SelectedCandidate);
        Assert.Equal("LDPlayer", vm.SelectedCandidate.EmulatorName);
        Assert.Equal(@"D:\LD\adb.exe", vm.SelectedCandidate.AdbPath);
    }

    [Fact]
    public void DeselectAll_LeavesSelectedCandidateNull()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };

        var vm = new SelectionDialogViewModel(candidates);
        vm.Items[0].IsSelected = true;
        vm.Items[0].IsSelected = false;

        Assert.Null(vm.SelectedCandidate);
    }





    [Fact]
    public void Confirm_WithSelection_ReturnsTrue()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
        };
        var vm = new SelectionDialogViewModel(candidates);

        var result = vm.ConfirmCommand.CanExecute(null);
        Assert.True(result);
    }

    [Fact]
    public void Confirm_WithoutSelection_CannotExecute()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };
        var vm = new SelectionDialogViewModel(candidates);

        var result = vm.ConfirmCommand.CanExecute(null);
        Assert.False(result);
    }

    [Fact]
    public void CancelCommand_AlwaysCanExecute()
    {
        var vm = new SelectionDialogViewModel([]);

        Assert.True(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void Confirm_ClosesDialogWithTrueResult()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
        };
        var vm = new SelectionDialogViewModel(candidates);

        bool? dialogResult = null;
        vm.RequestClose += (result) => dialogResult = result;

        vm.ConfirmCommand.Execute(null);

        Assert.True(dialogResult);
    }

    [Fact]
    public void Cancel_ClosesDialogWithFalseResult()
    {
        var vm = new SelectionDialogViewModel([]);

        bool? dialogResult = null;
        vm.RequestClose += (result) => dialogResult = result;

        vm.CancelCommand.Execute(null);

        Assert.False(dialogResult);
    }

    [Fact]
    public void Confirm_WithoutSelection_DoesNotCloseDialog()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };
        var vm = new SelectionDialogViewModel(candidates);

        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }






    [Fact]
    public void Cancel_DoesNotOverwriteDraft()
    {

        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };
        var vm = new SelectionDialogViewModel(candidates);


        vm.Items[1].IsSelected = true;
        Assert.NotNull(vm.SelectedCandidate);


        bool? dialogResult = null;
        vm.RequestClose += (result) => dialogResult = result;
        vm.CancelCommand.Execute(null);


        Assert.False(dialogResult);





    }

    [Fact]
    public void Confirm_ReturnsSelectedCandidate()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
        };
        var vm = new SelectionDialogViewModel(candidates);

        vm.Items[1].IsSelected = true;

        bool? dialogResult = null;
        vm.RequestClose += (result) => dialogResult = result;
        vm.ConfirmCommand.Execute(null);

        Assert.True(dialogResult);
        Assert.NotNull(vm.SelectedCandidate);
        Assert.Equal("LDPlayer", vm.SelectedCandidate.EmulatorName);
        Assert.Equal(@"D:\LD\adb.exe", vm.SelectedCandidate.AdbPath);
    }

    [Fact]
    public void SelectDifferentCandidate_UpdatesSelection()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("BlueStacks", @"C:\BS\HD-Adb.exe"),
            new("LDPlayer", @"D:\LD\adb.exe"),
            new("Nox", @"E:\Nox\nox_adb.exe"),
        };
        var vm = new SelectionDialogViewModel(candidates);


        vm.Items[0].IsSelected = true;
        Assert.Equal("BlueStacks", vm.SelectedCandidate!.EmulatorName);


        vm.Items[2].IsSelected = true;
        Assert.Equal("Nox", vm.SelectedCandidate!.EmulatorName);
        Assert.False(vm.Items[0].IsSelected);
        Assert.False(vm.Items[1].IsSelected);
    }

    [Fact]
    public void Title_UsesDynamicResource()
    {
        var vm = new SelectionDialogViewModel([]);


        Assert.Equal("SelectionDialogTitle", vm.TitleResourceKey);
    }





    [Fact]
    public void CandidateWithNullAdbPath_CanStillBeSelected()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("Unknown", null),
        };
        var vm = new SelectionDialogViewModel(candidates);

        vm.Items[0].IsSelected = true;

        Assert.NotNull(vm.SelectedCandidate);
        Assert.Null(vm.SelectedCandidate.AdbPath);
    }

    [Fact]
    public void Confirm_WithNullAdbPathCandidate_StillReturnsTrue()
    {
        var candidates = new List<DetectedEmulatorInfo>
        {
            new("Unknown", null),
        };
        var vm = new SelectionDialogViewModel(candidates);

        bool? dialogResult = null;
        vm.RequestClose += (result) => dialogResult = result;
        vm.ConfirmCommand.Execute(null);

        Assert.True(dialogResult);
    }
}
