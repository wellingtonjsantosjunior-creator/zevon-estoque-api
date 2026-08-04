using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using System.Security.Claims;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MovimentacoesController : ControllerBase
{
    private readonly string _connectionString;

    public MovimentacoesController(string connectionString)
    {
        _connectionString = connectionString;
    }

    private int GetIdUsuario() =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;

    // =========================================
    // ENTRADA
    // =========================================

    [HttpPost("entrada")]
    public async Task<IActionResult> Entrada([FromBody] MovimentacaoRequest request)
    {
        var quantidade = (int)Math.Round(request.Quantidade);
        if (quantidade <= 0)
            return BadRequest("Quantidade invalida.");

        using var conn = new NpgsqlConnection(_connectionString);

        try
        {
            var resultado = await conn.QueryFirstOrDefaultAsync(
                "SELECT * FROM sp_entrada_estoque(@id_produto, @id_filial, @id_prateleira, @id_usuario, @quantidade, @observacao, @numero_nf)",
                new
                {
                    id_produto = request.IdProduto,
                    id_filial = request.IdFilial,
                    id_prateleira = request.IdPrateleira,
                    id_usuario = request.IdUsuario ?? GetIdUsuario(),
                    quantidade,
                    observacao = request.Observacao,
                    numero_nf = request.NumeroNf
                });

            return Ok(resultado);
        }
        catch (PostgresException ex) when (ex.SqlState == "P0001")
        {
            return BadRequest(ex.MessageText);
        }
    }

    // =========================================
    // SAÍDA
    // =========================================

    [HttpPost("saida")]
    public async Task<IActionResult> Saida([FromBody] MovimentacaoRequest request)
    {
        var quantidade = (int)Math.Round(request.Quantidade);
        if (quantidade <= 0)
            return BadRequest("Quantidade invalida.");

        using var conn = new NpgsqlConnection(_connectionString);

        // Quando a prateleira é informada por código de barras, usa a rotina de baixa
        // por prateleira (mesma regra da tela de coletor).
        if (!string.IsNullOrWhiteSpace(request.CodigoPrateleira))
        {
            try
            {
                var porPrateleira = await conn.QueryFirstOrDefaultAsync(
                    "SELECT * FROM sp_saida_por_prateleira(@codigo_prateleira, @id_produto, @id_usuario, @quantidade, @observacao, @id_requisicao)",
                    new
                    {
                        codigo_prateleira = request.CodigoPrateleira,
                        id_produto = request.IdProduto,
                        id_usuario = request.IdUsuario ?? GetIdUsuario(),
                        quantidade,
                        observacao = request.Observacao,
                        id_requisicao = (int?)null
                    });

                return Ok(porPrateleira);
            }
            catch (PostgresException ex)
            {
                return BadRequest(ex.MessageText);
            }
        }

        // Saída direta na filial (sem prateleira)
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var saldoAtual = await conn.QueryFirstOrDefaultAsync<int?>(@"
            SELECT qtd_atual
            FROM EstoqueFilial
            WHERE id_produto = @IdProduto
              AND id_filial = @IdFilial
            FOR UPDATE",
            new { request.IdProduto, request.IdFilial }, tx);

        if (saldoAtual == null || saldoAtual < quantidade)
            return BadRequest("Saldo insuficiente.");

        var saldoApos = saldoAtual.Value - quantidade;

        await conn.ExecuteAsync(@"
            UPDATE EstoqueFilial
            SET qtd_atual = @SaldoApos
            WHERE id_produto = @IdProduto
              AND id_filial = @IdFilial",
            new { SaldoApos = saldoApos, request.IdProduto, request.IdFilial }, tx);

        var idMovimentacao = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Movimentacoes
                (id_produto, id_filial, id_prateleira, id_usuario, tipo,
                 quantidade, saldo_apos, data_hora, observacao, origem_scan)
            VALUES
                (@IdProduto, @IdFilial, @IdPrateleira, @IdUsuario, 'SAIDA',
                 @Quantidade, @SaldoApos, NOW(), @Observacao, false)
            RETURNING id_movimentacao",
            new
            {
                request.IdProduto,
                request.IdFilial,
                request.IdPrateleira,
                IdUsuario = request.IdUsuario ?? GetIdUsuario(),
                Quantidade = quantidade,
                SaldoApos = saldoApos,
                request.Observacao
            }, tx);

        await tx.CommitAsync();

        return Ok(new
        {
            idMovimentacao,
            idProduto = request.IdProduto,
            idFilial = request.IdFilial,
            saldoAnterior = saldoAtual.Value,
            saldoAtual = saldoApos,
            quantidade
        });
    }

    // =========================================
    // SALDO
    // =========================================

    [HttpGet("saldo")]
    public async Task<IActionResult> Saldo([FromQuery] int idFilial)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var saldo = await conn.QueryAsync(@"
            SELECT
                ef.id_produto AS ""idProduto"",
                p.nome,
                p.codigo_sku AS ""codigoSku"",
                p.unidade,
                ef.qtd_atual AS quantidade,
                ef.qtd_minima AS ""estoqueMinimo"",
                f.nome AS filial
            FROM EstoqueFilial ef
            INNER JOIN Produtos p ON ef.id_produto = p.id_produto
            INNER JOIN Filiais f ON ef.id_filial = f.id_filial
            WHERE ef.id_filial = @IdFilial
              AND p.ativo = true
            ORDER BY p.nome",
            new { IdFilial = idFilial });

        return Ok(saldo);
    }

    // =========================================
    // KARDEX
    // =========================================

    [HttpGet("kardex")]
    public async Task<IActionResult> Kardex(
        [FromQuery] int idFilial,
        [FromQuery] int? idProduto,
        [FromQuery] DateTime? dataInicio,
        [FromQuery] DateTime? dataFim)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var kardex = await conn.QueryAsync(
            "SELECT * FROM sp_kardex(@id_filial, @id_produto, @data_inicio, @data_fim)",
            new
            {
                id_filial = idFilial,
                id_produto = idProduto,
                data_inicio = dataInicio,
                data_fim = dataFim
            });

        return Ok(kardex);
    }

    // =========================================
    // CONSULTA ETIQUETA
    // =========================================

    [HttpGet("etiqueta/{codigo}")]
    public async Task<IActionResult> BuscarEtiqueta(string codigo)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var produto = await conn.QueryFirstOrDefaultAsync(@"
            SELECT
                p.id_produto AS ""idProduto"",
                p.nome,
                p.codigo_sku AS ""codigoSku"",
                e.codigo_barras AS ""codigoBarras"",
                p.unidade,
                e.id_filial AS ""idFilial"",
                ef.qtd_atual AS saldo
            FROM Produtos p
            LEFT JOIN Etiquetas e
                ON e.id_produto = p.id_produto
               AND e.ativo = true
            LEFT JOIN EstoqueFilial ef
                ON ef.id_produto = p.id_produto
               AND ef.id_filial = e.id_filial
            WHERE (e.codigo_barras = @Codigo OR p.codigo_sku = @Codigo)
              AND p.ativo = true
            LIMIT 1",
            new { Codigo = codigo });

        if (produto == null)
            return NotFound("Produto não encontrado.");

        return Ok(produto);
    }

    // =========================================
    // CONSULTA PRATELEIRA
    // =========================================

    [HttpGet("prateleira/{codigo}")]
    public async Task<IActionResult> BuscarPrateleira(string codigo)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var produtos = await conn.QueryAsync(@"
            SELECT
                p.id_produto AS ""idProduto"",
                p.nome,
                p.codigo_sku AS ""codigoSku"",
                p.unidade,
                ef.qtd_atual AS quantidade,
                pr.descricao AS prateleira,
                pr.codigo_barras AS ""codigoPrateleira"",
                pp.posicao
            FROM ProdutoPrateleira pp
            INNER JOIN Produtos p
                ON pp.id_produto = p.id_produto
            INNER JOIN Prateleiras pr
                ON pp.id_prateleira = pr.id_prateleira
            LEFT JOIN EstoqueFilial ef
                ON ef.id_produto = p.id_produto
               AND ef.id_filial = pp.id_filial
            WHERE pr.codigo_barras = @Codigo
              AND p.ativo = true
            ORDER BY p.nome",
            new { Codigo = codigo });

        return Ok(produtos);
    }
}

public class MovimentacaoRequest
{
    public int IdProduto { get; set; }
    public int IdFilial { get; set; }
    public int? IdPrateleira { get; set; }
    public string? CodigoPrateleira { get; set; }
    public int? IdUsuario { get; set; }
    public decimal Quantidade { get; set; }
    public string? Observacao { get; set; }
    public string? Usuario { get; set; }
    public string? NumeroNf { get; set; }
}
