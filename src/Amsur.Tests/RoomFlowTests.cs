using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// P0-8: кабинеты обязаны доходить SchoolData → ToProblemInput() → solver.
// До фикса ToProblemInput() дропал Rooms в [] (позиционные [], [] закрывали только
// daysOff/unavailability, rooms/roomCaps оставались дефолтными).
public sealed class RoomFlowTests
{
    private static SchoolData TinySchool() => SchoolDataImporter.Import(Guid.NewGuid(),
    [
        new LoadRow("5А", "Мат", 1, "Иванов", false, null, "101"),
        new LoadRow("5Б", "Мат", 1, "Петрова", false, null, "102"),
    ], daysCount: 1, slotsPerDay: 2);

    // 1. Regression на баг P0-8: комнаты из SchoolData попадают в ProblemInput.
    [Fact]
    public void RoomsNotDropped_RegressionP08()
    {
        var data = TinySchool();
        Assert.Equal(2, data.Rooms.Count); // предусловие: импорт комнаты создал

        var input = data.ToProblemInput();

        Assert.Equal(2, input.Rooms.Count);
        Assert.Equal(
            data.Rooms.Select(r => r.Id).OrderBy(id => id),
            input.Rooms.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(
            data.Rooms.Select(r => r.Name).OrderBy(n => n),
            input.Rooms.Select(r => r.Name).OrderBy(n => n));
    }

    // 2. Свойства кабинета и привязка CurriculumItem.RoomId не теряются.
    [Fact]
    public void RoomProperties_AndItemLink_Preserved()
    {
        var data = TinySchool();
        var input = data.ToProblemInput();

        foreach (var room in input.Rooms)
        {
            Assert.False(string.IsNullOrWhiteSpace(room.Name));
            Assert.Equal(30, room.PhysicalCapacity); // дефолт импортёра
            Assert.Equal(1, room.MaxSimultaneousGroups); // дефолт импортёра
        }
        var roomIds = new HashSet<Guid>(input.Rooms.Select(r => r.Id));
        Assert.All(data.Curriculum, item =>
        {
            Assert.NotNull(item.RoomId);
            Assert.Contains(item.RoomId!.Value, roomIds); // привязка ведёт на существующий кабинет
        });
    }

    // 3. Пустой список кабинетов — корректно, без исключений, проблема строится.
    [Fact]
    public void EmptyRooms_Ok()
    {
        var data = SchoolDataImporter.Import(Guid.NewGuid(),
        [
            new LoadRow("5А", "Мат", 1, "Иванов", false, null, null),
        ], daysCount: 1, slotsPerDay: 2);

        Assert.Empty(data.Rooms);
        var input = data.ToProblemInput();
        Assert.Empty(input.Rooms);
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        Assert.NotNull(problem);
        Assert.Empty(problem!.Rooms);
    }

    // 4. Solver действительно получает кабинеты: feasible + RoomId назначены + validator чист.
    [Fact]
    public async Task SolverReceivesRooms_AssignsAndValidates()
    {
        var data = TinySchool();
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput(),
            new SolverOptions(MaxTimeSeconds: 10, NumSearchWorkers: 1, RandomSeed: 5));
        Assert.Empty(errors);
        Assert.Equal(2, problem!.Rooms.Count);

        var result = await new OrToolsSolver().SolveAsync(problem);
        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.Equal(2, result.Placements.Count);
        Assert.All(result.Placements, p => Assert.NotNull(p.RoomId));
        Assert.True(PlacementValidator.Validate(problem, result.Placements).IsValid);
    }
}
