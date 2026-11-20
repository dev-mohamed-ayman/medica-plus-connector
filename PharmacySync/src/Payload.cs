using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PharmacySync
{
    /// <summary>
    /// Builds the exact JSON bodies the PowerShell scripts send:
    ///   single: { "products": [ ... ] }
    ///   multi : { "branches": [ { code, name, address, phone, products: [ ... ] } ] }
    /// </summary>
    public static class Payload
    {
        public static string Products(IList<ProductItem> products)
        {
            var sb = new StringBuilder(products.Count * 200 + 32);
            sb.Append("{\"products\":");
            WriteProducts(sb, products);
            sb.Append('}');
            return sb.ToString();
        }

        public static string Branch(BranchItem branch, IList<ProductItem> products)
        {
            var sb = new StringBuilder(products.Count * 200 + 256);
            sb.Append("{\"branches\":[{");
            Prop(sb, "code", branch.Code); sb.Append(',');
            Prop(sb, "name", branch.Name); sb.Append(',');
            Prop(sb, "address", branch.Address); sb.Append(',');
            Prop(sb, "phone", branch.Phone); sb.Append(',');
            Str(sb, "products"); sb.Append(':');
            WriteProducts(sb, products);
            sb.Append("}]}");
            return sb.ToString();
        }

        private static void WriteProducts(StringBuilder sb, IList<ProductItem> products)
        {
            sb.Append('[');
            for (int i = 0; i < products.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = products[i];
                sb.Append('{');
                Prop(sb, "code", p.Code);
                if (p.NameAr != null) { sb.Append(','); Prop(sb, "name_ar", p.NameAr); }
                if (p.NameEn != null) { sb.Append(','); Prop(sb, "name_en", p.NameEn); }
                sb.Append(",\"price\":").Append(Number(p.Price));
                sb.Append(",\"quantity\":").Append(p.Quantity.ToString(CultureInfo.InvariantCulture));
                sb.Append(','); Prop(sb, "international_barcode", p.Barcode);
                sb.Append(','); Prop(sb, "image", p.Image);
                sb.Append('}');
            }
            sb.Append(']');
        }

        private static string Number(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void Prop(StringBuilder sb, string name, string value)
        {
            Str(sb, name);
            sb.Append(':');
            Str(sb, value ?? "");
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
