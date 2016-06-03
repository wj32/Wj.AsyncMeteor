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

    public static class ErrorOrResultExtensions
    {
        public static ErrorOrResult<U> Bind<T, U>(this ErrorOrResult<T> eor, Func<T, ErrorOrResult<U>> f)
        {
            if (eor.Error == null)
                return f(eor.Result);
            else
                return new ErrorOrResult<U> { Error = eor.Error, Result = default(U) };
        }

        public static ErrorOrResult<T> Join<T>(this ErrorOrResult<ErrorOrResult<T>> eor)
        {
            return eor.Bind(x => x);
        }

        public static ErrorOrResult<U> Map<T, U>(this ErrorOrResult<T> eor, Func<T, U> f)
        {
            return eor.Bind(x => new ErrorOrResult<U> { Result = f(x) });
        }
    }
}
