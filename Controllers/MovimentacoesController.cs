using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;

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

    // =========================================
    // ENTRADA
    // =========================================

    [HttpPost("entrada")]
    public async Task<IActionResult> Entrada([FromBody] MovimentacaoRequest request)
    {
        using var conn = new SqlConnection(_connectionString);

        var saldoAtual = await conn.QueryFirstOrDefaultAsync<decimal?>(@"
            SELECT quantidade
            FROM EstoqueSaldo
            WHERE id_produto = @IdProduto
              AND id_filial = @IdFilial",
            new
            {
                request.IdProduto,
                request.IdFilial
            });

        if (saldoAtual == null)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO EstoqueSaldo
                (id_produto, id_filial, quantidade)
                VALUES
                (@IdProduto, @IdFilial, @Quantidade)",
                new
                {
                    request.IdProduto,
                    request.IdFilial,
                    request.Quantidade
                });
        }
        else
        {
            await conn.ExecuteAsync(@"
                UPDATE EstoqueSaldo
                SET quantidade = quantidade + @Quantidade
                WHERE id_produto = @IdProduto
                  AND id_filial = @IdFilial",
                new
                {
                    request.IdProduto,
                    request.IdFilial,
                    request.Quantidade
                });
        }

        await conn.ExecuteAsync(@"
            INSERT INTO MovimentacoesEstoque
            (
                tipo,
                id_produto,
                id_filial,
                quantidade,
                observacao,
                usuario,
                data_movimentacao
            )
            VALUES
            (
                'ENTRADA',
                @IdProduto,
                @IdFilial,
                @Quantidade,
                @Observacao,
                @Usuario,
                GETDATE()
            )",
            request);

        return Ok("Entrada registrada com sucesso.");
    }

    // =========================================
    // SAÍDA
    // =========================================

    [HttpPost("saida")]
    public async Task<IActionResult> Saida([FromBody] MovimentacaoRequest request)
    {
        using var conn = new SqlConnection(_connectionString);

        var saldoAtual = await conn.QueryFirstOrDefaultAsync<decimal?>(@"
            SELECT quantidade
            FROM EstoqueSaldo
            WHERE id_produto = @IdProduto
              AND id_filial = @IdFilial",
            new
            {
                request.IdProduto,
                request.IdFilial
            });

        if (saldoAtual == null || saldoAtual < request.Quantidade)
        {
            return BadRequest("Saldo insuficiente.");
        }

        await conn.ExecuteAsync(@"
            UPDATE EstoqueSaldo
            SET quantidade = quantidade - @Quantidade
            WHERE id_produto = @IdProduto
              AND id_filial = @IdFilial",
            new
            {
                request.IdProduto,
                request.IdFilial,
                request.Quantidade
            });

        await conn.ExecuteAsync(@"
            INSERT INTO MovimentacoesEstoque
            (
                tipo,
                id_produto,
                id_filial,
                quantidade,
                observacao,
                usuario,
                data_movimentacao
            )
            VALUES
            (
                'SAIDA',
                @IdProduto,
                @IdFilial,
                @Quantidade,
                @Observacao,
                @Usuario,
                GETDATE()
            )",
            request);

        return Ok("Saída registrada com sucesso.");
    }

    // =========================================
    // SALDO
    // =========================================

    [HttpGet("saldo")]
    public async Task<IActionResult> Saldo([FromQuery] int idFilial)
    {
        using var conn = new SqlConnection(_connectionString);

        var saldo = await conn.QueryAsync(@"
            SELECT
                es.id_produto AS idProduto,
                p.nome,
                p.codigo_sku AS codigoSku,
                p.unidade_medida AS unidade,
                es.quantidade,
                f.nome AS filial
            FROM EstoqueSaldo es
            INNER JOIN Produtos p
                ON es.id_produto = p.id_produto
            INNER JOIN Filiais f
                ON es.id_filial = f.id_filial
            WHERE es.id_filial = @IdFilial
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
        [FromQuery] int? idProduto)
    {
        using var conn = new SqlConnection(_connectionString);

        var kardex = await conn.QueryAsync(@"
            SELECT
                m.id_movimentacao AS idMovimentacao,
                m.tipo,
                p.nome AS produto,
                p.codigo_sku AS codigoSku,
                m.quantidade,
                m.observacao,
                m.usuario,
                m.data_movimentacao AS dataMovimentacao
            FROM MovimentacoesEstoque m
            INNER JOIN Produtos p
                ON m.id_produto = p.id_produto
            WHERE m.id_filial = @IdFilial
              AND (@IdProduto IS NULL
                   OR m.id_produto = @IdProduto)
            ORDER BY m.data_movimentacao DESC",
            new
            {
                IdFilial = idFilial,
                IdProduto = idProduto
            });

        return Ok(kardex);
    }

    // =========================================
    // CONSULTA ETIQUETA
    // =========================================

    [HttpGet("etiqueta/{codigo}")]
    public async Task<IActionResult> BuscarEtiqueta(string codigo)
    {
        using var conn = new SqlConnection(_connectionString);

        var produto = await conn.QueryFirstOrDefaultAsync(@"
            SELECT TOP 1
                p.id_produto AS idProduto,
                p.nome,
                p.codigo_sku AS codigoSku,
                p.codigo_barras AS codigoBarras,
                p.unidade_medida AS unidade
            FROM Produtos p
            WHERE p.codigo_barras = @Codigo
               OR p.codigo_sku = @Codigo",
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
        using var conn = new SqlConnection(_connectionString);

        var produtos = await conn.QueryAsync(@"
            SELECT
                p.nome,
                p.codigo_sku AS codigoSku,
                es.quantidade,
                pr.descricao AS prateleira,
                pp.posicao
            FROM ProdutoPrateleira pp
            INNER JOIN Produtos p
                ON pp.id_produto = p.id_produto
            INNER JOIN Prateleiras pr
                ON pp.id_prateleira = pr.id_prateleira
            LEFT JOIN EstoqueSaldo es
                ON es.id_produto = p.id_produto
               AND es.id_filial = pp.id_filial
            WHERE pr.codigo_barras = @Codigo
            ORDER BY p.nome",
            new { Codigo = codigo });

        return Ok(produtos);
    }
}

public class MovimentacaoRequest
{
    public int IdProduto { get; set; }
    public int IdFilial { get; set; }
    public decimal Quantidade { get; set; }
    public string? Observacao { get; set; }
    public string? Usuario { get; set; }
}