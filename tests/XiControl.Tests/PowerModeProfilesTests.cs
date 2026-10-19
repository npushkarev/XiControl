using System.Text.Json;
using FluentAssertions;
using XiControl.SystemIntegration;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

public sealed class PowerModeProfilesTests
{
    private const string ValidJson = """
        [{"Mode":"Quiet","OnAc":true,"Pl1Watts":25,"Pl2Watts":40},
         {"Mode":"Quiet","OnAc":false,"Pl1Watts":10,"Pl2Watts":20}]
        """;

    [Fact]
    public void Parse_разделяет_режим_и_источник_питания()
    {
        var entries = PowerModeProfiles.Parse(ValidJson);
        entries.Should().Equal(new PowerModeProfile(PerfMode.Quiet, true, 25, 40),
            new PowerModeProfile(PerfMode.Quiet, false, 10, 20));
        PowerModeProfiles.Find(entries, PerfMode.Quiet, true).Should().Be(entries[0]);
        PowerModeProfiles.Find(entries, PerfMode.Quiet, false).Should().Be(entries[1]);
        PowerModeProfiles.Find(entries, PerfMode.Turbo, true).Should().BeNull();
    }

    [Fact]
    public void Parse_и_Validate_принимают_пустой_список()
    {
        PowerModeProfiles.Parse("[]").Should().BeEmpty();
        Action validate = () => PowerModeProfiles.Validate([]);
        validate.Should().NotThrow();
    }

    [Fact]
    public void Все_шесть_режимов_имеют_два_независимых_пресета()
    {
        var entries = Enum.GetValues<PerfMode>()
            .SelectMany(mode => new[] { new PowerModeProfile(mode, true, 10, 250), new PowerModeProfile(mode, false, 10, 10) })
            .ToArray();
        entries.Should().HaveCount(12);
        Action validate = () => PowerModeProfiles.Validate(entries);
        validate.Should().NotThrow();
        PowerModeProfiles.Parse(PowerModeProfiles.Serialize(entries)).Should().Equal(entries);
    }

    [Theory]
    [InlineData(9, 40)]
    [InlineData(10, 251)]
    [InlineData(251, 251)]
    [InlineData(10, 9)]
    [InlineData(41, 40)]
    [InlineData(-1, 20)]
    public void Validate_отвергает_границы_и_PL1_выше_PL2(int pl1, int pl2)
    {
        Action validate = () => PowerModeProfiles.Validate([new(PerfMode.Turbo, true, pl1, pl2)]);
        validate.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(255)]
    public void Validate_не_принимает_неопределённый_enum(int mode)
    {
        Action validate = () => PowerModeProfiles.Validate([new((PerfMode)mode, true, 20, 30)]);
        validate.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Validate_отвергает_null_и_повтор_ключа()
    {
        Action nullList = () => PowerModeProfiles.Validate(null!);
        Action nullEntry = () => PowerModeProfiles.Validate([null!]);
        Action duplicate = () => PowerModeProfiles.Validate([
            new(PerfMode.Quiet, true, 20, 30), new(PerfMode.Quiet, true, 10, 20)]);
        nullList.Should().Throw<ArgumentNullException>();
        nullEntry.Should().Throw<InvalidDataException>();
        duplicate.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[")]
    [InlineData("[] trailing")]
    [InlineData("[{\"Mode\":\"Quiet\",\"OnAc\":true,\"Pl1Watts\":20,\"Pl2Watts\":30,}]")]
    public void Parse_не_скрывает_неверную_структуру(string json)
    {
        Action parse = () => PowerModeProfiles.Parse(json);
        parse.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("\"Mode\":\"Quiet\"", "\"Mode\":\"FutureMode\"")]
    [InlineData("\"Mode\":\"Quiet\"", "\"Mode\":2")]
    [InlineData("\"Mode\":\"Quiet\"", "\"Mode\":null")]
    [InlineData("\"Mode\":\"Quiet\"", "\"Mode\":\"2\"")]
    [InlineData("\"OnAc\":true", "\"OnAc\":null")]
    [InlineData("\"OnAc\":true", "\"OnAc\":1")]
    [InlineData("\"Pl1Watts\":25", "\"Pl1Watts\":null")]
    [InlineData("\"Pl1Watts\":25", "\"Pl1Watts\":\"25\"")]
    [InlineData("\"Pl1Watts\":25", "\"Pl1Watts\":25.5")]
    [InlineData("\"Pl2Watts\":40", "\"Pl2Watts\":2147483648")]
    [InlineData("\"Pl2Watts\":40", "\"Pl2Watts\":40,\"Unknown\":true")]
    [InlineData("\"Pl2Watts\":40", "\"Pl2Watts\":40,\"Pl2Watts\":50")]
    public void Parse_отвергает_неизвестное_null_и_подмену_типов(string before, string after)
    {
        Action parse = () => PowerModeProfiles.Parse(ValidJson.Replace(before, after, StringComparison.Ordinal));
        parse.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("\"Mode\":\"Quiet\",")]
    [InlineData("\"OnAc\":true,")]
    [InlineData("\"Pl1Watts\":25,")]
    [InlineData(",\"Pl2Watts\":40")]
    public void Parse_не_подставляет_дефолт_за_пропущенное_поле(string field)
    {
        Action parse = () => PowerModeProfiles.Parse(ValidJson.Replace(field, "", StringComparison.Ordinal));
        parse.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("\"OnAc\":false", "\"OnAc\":true")]
    [InlineData("\"Pl1Watts\":25", "\"Pl1Watts\":9")]
    [InlineData("\"Pl2Watts\":40", "\"Pl2Watts\":251")]
    [InlineData("\"Pl1Watts\":25", "\"Pl1Watts\":41")]
    public void Parse_проверяет_инварианты_после_JSON(string before, string after)
    {
        Action parse = () => PowerModeProfiles.Parse(ValidJson.Replace(before, after, StringComparison.Ordinal));
        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_ограничивает_число_записей_и_размер()
    {
        var entries = Enumerable.Repeat(new PowerModeProfile(PerfMode.Quiet, true, 20, 30), 13).ToArray();
        Action validate = () => PowerModeProfiles.Validate(entries);
        Action parse = () => PowerModeProfiles.Parse(JsonSerializer.Serialize(entries));
        Action huge = () => PowerModeProfiles.Parse(new string(' ', 16385) + "[]");
        Action nullJson = () => PowerModeProfiles.Parse(null!);
        validate.Should().Throw<InvalidDataException>();
        parse.Should().Throw<InvalidDataException>();
        huge.Should().Throw<InvalidDataException>();
        nullJson.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Upsert_заменяет_только_свой_ключ_не_меняя_исходник()
    {
        var entries = PowerModeProfiles.Parse(ValidJson);
        var changed = PowerModeProfiles.Upsert(entries, new(PerfMode.Quiet, true, 30, 50));
        changed.Should().Equal(new PowerModeProfile(PerfMode.Quiet, true, 30, 50), entries[1]);
        entries[0].Pl1Watts.Should().Be(25);
        var added = PowerModeProfiles.Upsert(changed, new(PerfMode.Eco, false, 10, 15));
        added.Should().HaveCount(3);
        changed.Should().HaveCount(2);
        Action mutate = () => ((IList<PowerModeProfile>)added).Add(new(PerfMode.Auto, false, 10, 15));
        mutate.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Remove_не_затрагивает_другой_источник_и_исходник()
    {
        var entries = PowerModeProfiles.Parse(ValidJson);
        PowerModeProfiles.Remove(entries, PerfMode.Quiet, true).Should().Equal(entries[1]);
        PowerModeProfiles.Remove(entries, PerfMode.Eco, false).Should().Equal(entries);
        entries.Should().HaveCount(2);
    }

    [Fact]
    public void Helpers_не_нормализуют_повреждённый_вход_молча()
    {
        PowerModeProfile[] bad = [new(PerfMode.Quiet, true, 20, 30), new(PerfMode.Quiet, true, 20, 30)];
        Action find = () => PowerModeProfiles.Find(bad, PerfMode.Quiet, true);
        Action remove = () => PowerModeProfiles.Remove(bad, PerfMode.Quiet, true);
        Action upsert = () => PowerModeProfiles.Upsert(bad, new(PerfMode.Quiet, true, 10, 20));
        Action invalidNew = () => PowerModeProfiles.Upsert([], new(PerfMode.Quiet, true, 40, 20));
        Action nullNew = () => PowerModeProfiles.Upsert([], null!);
        Action invalidFind = () => PowerModeProfiles.Find([], (PerfMode)255, true);
        Action invalidRemove = () => PowerModeProfiles.Remove([], (PerfMode)255, true);
        find.Should().Throw<InvalidDataException>();
        remove.Should().Throw<InvalidDataException>();
        upsert.Should().Throw<InvalidDataException>();
        invalidNew.Should().Throw<InvalidDataException>();
        nullNew.Should().Throw<ArgumentNullException>();
        invalidFind.Should().Throw<InvalidDataException>();
        invalidRemove.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Serialize_пишет_имена_enum_и_проходит_строгий_Parse()
    {
        var entries = PowerModeProfiles.Parse(ValidJson);
        string json = PowerModeProfiles.Serialize(entries);
        json.Should().Contain("\"Mode\": \"Quiet\"");
        PowerModeProfiles.Parse(json).Should().Equal(entries);
    }
}
