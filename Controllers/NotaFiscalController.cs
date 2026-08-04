using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotaFiscalController : ControllerBase
{
    private static readonly XNamespace Ns = "http://www.portalfiscal.inf.br/nfe";
    private readonly string _connectionString;

    public NotaFiscalController(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── IMPORTAR XML DA NFe ────────────────────────────────────────
    // Lê o XML, extrai fornecedor e itens, e tenta casar cada item com um
    // produto já cadastrado (por codigo_sku) e o fornecedor por CNPJ.
    // Não grava nada no banco — a confirmação (entrada de estoque) é feita
    // item a item pelo app, depois da revisão do usuário.
    [HttpPost("importar-xml")]
    public async Task<IActionResult> ImportarXml([FromBody] ImportarNfeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Xml))
            return BadRequest("Envie o conteúdo do XML.");

        XDocument doc;
        try
        {
            doc = XDocument.Parse(request.Xml);
        }
        catch (Exception ex)
        {
            return BadRequest($"XML inválido: {ex.Message}");
        }

        var infNFe = doc.Descendants(Ns + "infNFe").FirstOrDefault();
        if (infNFe == null)
            return BadRequest("Não parece ser um XML de NFe (elemento infNFe não encontrado).");

        var ide = infNFe.Element(Ns + "ide");
        var numeroNf = ide?.Element(Ns + "nNF")?.Value;

        var emit = infNFe.Element(Ns + "emit");
        var cnpjEmit = emit?.Element(Ns + "CNPJ")?.Value;
        var nomeEmit = emit?.Element(Ns + "xNome")?.Value;

        using var conn = new NpgsqlConnection(_connectionString);

        var fornecedorEncontrado = string.IsNullOrWhiteSpace(cnpjEmit)
            ? null
            : await conn.QueryFirstOrDefaultAsync(
                "SELECT id_fornecedor AS \"idFornecedor\", nome FROM Fornecedores WHERE cnpj = @Cnpj LIMIT 1",
                new { Cnpj = cnpjEmit });

        var itens = new List<object>();
        foreach (var det in infNFe.Elements(Ns + "det"))
        {
            var prod = det.Element(Ns + "prod");
            if (prod == null) continue;

            var cProd = prod.Element(Ns + "cProd")?.Value;
            var cEan = prod.Element(Ns + "cEAN")?.Value;
            var xProd = prod.Element(Ns + "xProd")?.Value ?? "(sem descrição)";
            var uCom = prod.Element(Ns + "uCom")?.Value;

            var qtd = ParseDecimal(prod.Element(Ns + "qCom")?.Value);
            var valorUnitario = ParseDecimal(prod.Element(Ns + "vUnCom")?.Value);
            var valorTotal = ParseDecimal(prod.Element(Ns + "vProd")?.Value);

            var produtoEncontrado = string.IsNullOrWhiteSpace(cProd)
                ? null
                : await conn.QueryFirstOrDefaultAsync(
                    "SELECT id_produto AS \"idProduto\", nome FROM Produtos WHERE codigo_sku = @Cod AND ativo = true LIMIT 1",
                    new { Cod = cProd });

            itens.Add(new
            {
                codigoProduto = cProd,
                codigoBarras = (cEan == "SEM GTIN" ? null : cEan),
                descricao = xProd,
                unidade = uCom,
                quantidade = qtd,
                valorUnitario,
                valorTotal,
                idProdutoEncontrado = produtoEncontrado?.idProduto,
                nomeProdutoEncontrado = produtoEncontrado?.nome
            });
        }

        if (itens.Count == 0)
            return BadRequest("Nenhum item (det/prod) encontrado nesse XML.");

        return Ok(new
        {
            numeroNf,
            fornecedor = new
            {
                cnpj = cnpjEmit,
                nome = nomeEmit,
                idFornecedorEncontrado = fornecedorEncontrado?.idFornecedor,
                nomeFornecedorEncontrado = fornecedorEncontrado?.nome
            },
            itens
        });
    }

    private static decimal ParseDecimal(string? valor) =>
        decimal.TryParse(valor, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

public class ImportarNfeRequest
{
    public string Xml { get; set; } = string.Empty;
}
