using Microsoft.AspNetCore.Mvc;
using PNMTD.Models.Db;

namespace PNMTD.Helper
{
    public class HttpResultHelper
    {

        public static object ReturnSinglePocoOrNotFound<T, E>(IQueryable<T> query, Func<T, E> action)
        {
            if(query.Count() == 1)
            {
                return query.Select(t => action.Invoke(t)).Single();
            }
            return new NotFoundResult();
        }

    }
}
