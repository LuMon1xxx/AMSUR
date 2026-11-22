using Amsur.Application;

namespace Amsur.Tests;

// Подсветка этапов на экране генерации: после завершения ни один этап не
// должен оставаться «текущим» — иначе экран выглядит зависшим на последнем шаге.
public sealed class StageHighlightTests
{
    private static GenerationProgressDto Dto(GenerationPhase phase) =>
        new(phase, TimeSpan.FromSeconds(3), null, null, 0, 0, 5, "seed 11", "…", false,
            TimeSpan.FromSeconds(1));

    [Fact]
    public void Done_NoStageIsCurrent_AllDone()
    {
        var vm = new GenerateViewModel();
        vm.ApplyProgress(Dto(GenerationPhase.Done));
        Assert.DoesNotContain(vm.Stages, s => s.IsCurrent);
        Assert.All(vm.Stages, s => Assert.True(s.IsDone));
    }

    [Fact]
    public void Stopped_NoStageIsCurrent()
    {
        var vm = new GenerateViewModel();
        vm.ApplyProgress(Dto(GenerationPhase.Stopped));
        Assert.DoesNotContain(vm.Stages, s => s.IsCurrent);
    }

    [Fact]
    public void Improving_MarksThirdStageCurrent_PreviousDone()
    {
        var vm = new GenerateViewModel();
        vm.ApplyProgress(Dto(GenerationPhase.Improving));
        Assert.Equal(2, vm.Stages.ToList().FindIndex(s => s.IsCurrent));
        Assert.Equal(2, vm.Stages.Count(s => s.IsDone));
        Assert.False(vm.Stages[3].IsDone);
    }

    [Fact]
    public void Preparing_FirstStageCurrent()
    {
        var vm = new GenerateViewModel();
        vm.ApplyProgress(Dto(GenerationPhase.Preparing));
        Assert.True(vm.Stages[0].IsCurrent);
        Assert.DoesNotContain(vm.Stages, s => s.IsDone);
    }
}

