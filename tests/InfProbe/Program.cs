using System.Security.Cryptography;
using System.Text;
using MutaForTuneLab.Assets;

// Prints the canonical dump digest of an .inf file. The digest is what
// tools/verify-inf.ps1 compares against the Python reference parser.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: InfProbe <file.inf>");
    return 2;
}

try
{
    var info = InfFile.Load(args[0]);
    var text = Dump(info);
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    Console.WriteLine($"version={info.Version} items={info.ItemCount} mgc={info.MgcOrder} " +
                      $"bap={info.BapOrder} final={info.FinalCount} groups={info.GroupCount}");
    Console.WriteLine($"parsed_items={info.TotalItems}");
    Console.WriteLine($"sha256={hash}");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static string Dump(InfFile info)
{
    var sb = new StringBuilder();
    sb.Append('v').Append(info.Version)
      .Append(" n").Append(info.ItemCount)
      .Append(" mgc").Append(info.MgcOrder)
      .Append(" bap").Append(info.BapOrder)
      .Append(" final").Append(info.FinalCount)
      .Append(" groups").Append(info.GroupCount).Append('\n');

    foreach (var group in info.Groups)
        sb.Append("g ").Append(group.Name).Append(' ').Append(group.DeclaredCount)
          .Append(' ').Append(group.Items.Count).Append('\n');

    foreach (var group in info.Groups)
    {
        foreach (var item in group.Items)
        {
            sb.Append("i ").Append(item.Current).Append(' ')
              .Append(item.Left).Append(' ').Append(item.Right).Append(' ');
            for (int k = 0; k < item.Values.Length; k++)
            {
                if (k > 0)
                    sb.Append(' ');
                sb.Append(item.Values[k]);
            }
            sb.Append('\n');
        }
    }

    foreach (var (count, start, size) in info.Final)
        sb.Append("f ").Append(count).Append(' ').Append(start).Append(' ').Append(size).Append('\n');

    return sb.ToString();
}
