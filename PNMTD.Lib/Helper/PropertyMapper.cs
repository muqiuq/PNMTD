using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Helper
{
    public static class PropertyMapper
    {

        public static W MapPropertiesTo<T,W>(this T t) where W : new()
        {
            var w = new W();

            var propertiesOfW = typeof(W).GetProperties();

            foreach( var p in typeof(T).GetProperties())
            {
                var selectProperty = propertiesOfW.Where(x => x.Name == p.Name);
                if (selectProperty.Any())
                {
                    selectProperty.Single().SetValue(w, p.GetValue(t));
                }
            }

            return w;
        }

    }
}
