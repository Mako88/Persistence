using Dapper;
using System.Data;

namespace Persistence.Data.TypeMappers;

/// <summary>
/// Dapper type handler that stores enum values as their string name rather than
/// their underlying integer. Ensures human-readable enum storage in SQLite.
/// </summary>
public class EnumTypeHandler<T> : SqlMapper.TypeHandler<T> where T : struct, Enum
{
    public override void SetValue(IDbDataParameter parameter, T value) => parameter.Value = value.ToString();

    public override T Parse(object value) =>
        Enum.Parse<T>(value.ToString()!);
}
