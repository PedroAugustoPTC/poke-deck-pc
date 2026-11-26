using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PokeDeck.Core;

/// <summary>
/// Extensões da Chrome Web Store (como o PokeIdleMulti faz): baixa o .crx, tira o zip de dentro e põe a chave pública
/// no manifest, para a extensão manter o mesmo ID da loja (e o armazenamento dela entre reinstalações).
/// </summary>
public static class Crx
{
    public static string? IdFromLink(string linkOrId) => Regex.Match(linkOrId, "[a-p]{32}") is { Success: true } m ? m.Value : null;

    public static string DownloadUrl(string id) =>
        "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=131.0.0.0" +
        $"&acceptformat=crx2,crx3&x=id%3D{id}%26installsource%3Dondemand%26uc";

    /// <summary>O ID que o Chrome dá a uma chave pública: SHA-256, 16 primeiros bytes, cada meio byte vira a..p.</summary>
    public static string IdFromKey(byte[] key)
    {
        var hash = SHA256.HashData(key);
        var sb = new StringBuilder(32);
        foreach (var b in hash.AsSpan(0, 16))
            sb.Append((char)('a' + (b >> 4))).Append((char)('a' + (b & 15)));
        return sb.ToString();
    }

    /// <summary>Onde começa o zip dentro do .crx e a chave pública da extensão de <paramref name="id"/>.</summary>
    public static (int ZipStart, byte[]? PublicKey) Parse(byte[] crx, string id)
    {
        if (crx.Length < 16 || Encoding.ASCII.GetString(crx, 0, 4) != "Cr24")
            throw new InvalidDataException("A loja não devolveu uma extensão (.crx). Confira o link.");
        uint version = BitConverter.ToUInt32(crx, 4);
        if (version == 2)
        {
            int keyLength = (int)BitConverter.ToUInt32(crx, 8), sigLength = (int)BitConverter.ToUInt32(crx, 12);
            return (16 + keyLength + sigLength, crx[16..(16 + keyLength)]);
        }
        if (version == 3)
        {
            int headerLength = (int)BitConverter.ToUInt32(crx, 8);
            var header = crx[12..(12 + headerLength)];
            // CrxFileHeader (protobuf): campos 2 e 3 são as provas de chave; dentro delas, o campo 1 é a chave pública.
            var key = Proto(header).Where(f => f.Field is 2 or 3).SelectMany(f => Proto(f.Data))
                .Where(f => f.Field == 1).Select(f => f.Data).FirstOrDefault(k => IdFromKey(k) == id);
            return (12 + headerLength, key);
        }
        throw new InvalidDataException($"Formato de .crx desconhecido (versão {version}).");
    }

    private static List<(int Field, byte[] Data)> Proto(byte[] buf)
    {
        var fields = new List<(int, byte[])>();
        int pos = 0;
        while (pos < buf.Length)
        {
            ulong tag = Varint(buf, ref pos);
            switch ((int)(tag & 7))
            {
                case 0: Varint(buf, ref pos); break;
                case 1: pos += 8; break;
                case 2:
                    int len = (int)Varint(buf, ref pos);
                    fields.Add(((int)(tag >> 3), buf[pos..(pos + len)]));
                    pos += len;
                    break;
                case 5: pos += 4; break;
                default: return fields;
            }
        }
        return fields;
    }

    private static ulong Varint(byte[] buf, ref int pos)
    {
        ulong value = 0;
        for (int shift = 0; pos < buf.Length; shift += 7)
        {
            byte b = buf[pos++];
            value |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80)
                break;
        }
        return value;
    }

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Descompacta o .crx em <paramref name="root"/>\&lt;id&gt;_&lt;versão&gt; (pasta nunca reescrita depois: o WebView2
    /// desinstala a extensão se o conteúdo mudar). Devolve a pasta.
    /// </summary>
    public static string Unpack(byte[] crx, string id, string root)
    {
        var (zipStart, key) = Parse(crx, id);
        var temp = Path.Combine(root, "_baixando_" + id);
        if (Directory.Exists(temp))
            Directory.Delete(temp, true);
        using (var zip = new ZipArchive(new MemoryStream(crx, zipStart, crx.Length - zipStart)))
            zip.ExtractToDirectory(temp);
        var meta = Path.Combine(temp, "_metadata"); // assinatura da loja: o WebView2 recusa a pasta com ela
        if (Directory.Exists(meta))
            Directory.Delete(meta, true);
        var manifestPath = Path.Combine(temp, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath), documentOptions: Lenient)!.AsObject();
        if (key is not null && !manifest.ContainsKey("key"))
        {
            manifest["key"] = Convert.ToBase64String(key);
            File.WriteAllText(manifestPath, manifest.ToJsonString());
        }
        var version = Regex.Replace(manifest["version"]?.ToString() ?? "0", "[^0-9.]", "");
        var dest = Path.Combine(root, $"{id}_{version}");
        if (Directory.Exists(dest))
            Directory.Delete(temp, true);
        else
            Directory.Move(temp, dest);
        return dest;
    }

    /// <summary>A pasta mais nova de uma extensão instalada pelo app (null: não foi o app que instalou).</summary>
    public static string? FolderOf(string root, string id) =>
        Directory.Exists(root) ? Directory.GetDirectories(root, id + "_*").OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() : null;

    /// <summary>
    /// A página que o botão da extensão abre: o popup do ícone; sem popup, a única página .html da extensão (o Cockpit do
    /// Rotom Sniper, por exemplo), que abre numa janela grande (<c>Window</c> = true).
    /// </summary>
    public static (string? Page, bool Window) Popup(string folder)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")), documentOptions: Lenient)!.AsObject();
        var action = manifest["action"] ?? manifest["browser_action"] ?? manifest["page_action"];
        var popup = action?["default_popup"]?.ToString().TrimStart('/');
        if (!string.IsNullOrEmpty(popup))
            return (popup, false);
        if (action is null)
            return (null, false);
        var options = (manifest["options_ui"]?["page"] ?? manifest["options_page"])?.ToString().TrimStart('/');
        var background = manifest["background"]?["page"]?.ToString().TrimStart('/');
        var pages = Directory.GetFiles(folder, "*.html").Select(Path.GetFileName).Where(f => f != options && f != background).ToList();
        return pages.Count == 1 ? (pages[0], true) : (null, false);
    }
}
