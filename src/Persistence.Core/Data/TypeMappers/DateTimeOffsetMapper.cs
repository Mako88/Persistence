using Dapper;
using System.Data;

namespace Persistence.Data.TypeMappers
{
    public class DateTimeOffsetMapper : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.Value = value;
        }

        public override DateTimeOffset Parse(object value)
        {
            if (value is string s && DateTimeOffset.TryParse(s, out var result))
            {
                return result;
            }
            return DateTimeOffset.Parse(value.ToString() ?? "");
        }
    }
}
