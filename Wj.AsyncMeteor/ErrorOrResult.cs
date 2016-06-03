using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wj.AsyncMeteor
{
    public class ErrorOrResult<T>
    {
        public dynamic Error;
        public T Result;
    }

    public class ErrorOrResult : ErrorOrResult<dynamic>
    { }
}
