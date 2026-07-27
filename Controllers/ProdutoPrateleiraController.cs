using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProdutoPrateleiraController : ControllerBase
{
    private readonly string _connectionString;

    public ProdutoPrateleiraController(string connectionString)
    {
        _connectionString = connectionString;
    }

    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] int? idFilial,
        [FromQuery] int? idProduto,
        [FromQuery] int? idPrateleira)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var vinculos = await conn.QueryAsync(@"
            SELECT
                pp.id AS id,
                pp.id_produto AS idProduto,
                p.nome AS produto,
                p.codigo_sku AS codigoSku,
                pp.id_prateleira AS idPrateleira,
                pr.codigo_barras AS codigoPrateleira,
                pr.descricao AS prateleira,
                pp.id_filial AS idFilial,
                f.nome AS filial,
                pp.posicao
            FROM ProdutoPrateleira pp
            INNER JOIN Produtos p ON pp.id_produto = p.id_produto
            INNER JOIN Prateleiras pr ON pp.id_prateleira = pr.id_prateleira
            INNER JOIN Filiais f ON pp.id_filial = f.id_filial
            WHERE (@IdFilial IS NULL OR pp.id_filial = @IdFilial)
              AND (@IdProduto IS NULL OR pp.id_produto = @IdProduto)
              AND (@IdPrateleira IS NULL OR pp.id_prateleira = @IdPrateleira)
            ORDER BY f.nome, pr.descricao, p.nome",
            new
            {
                IdFilial = idFilial,
                IdProduto = idProduto,
                IdPrateleira = idPrateleira
            });

        return Ok(vinculos);
    }

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] ProdutoPrateleiraRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            INSERT INTO ProdutoPrateleira
            (id_produto, id_prateleira, id_filial, posicao)
            VALUES
            (@IdProduto, @IdPrateleira, @IdFilial, @Posicao)",
            request);

        return Ok("Produto vinculado à prateleira com sucesso.");
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Atualizar(
        int id,
        [FromBody] ProdutoPrateleiraRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE ProdutoPrateleira
            SET id_produto = @IdProduto,
                id_prateleira = @IdPrateleira,
                id_filial = @IdFilial,
                posicao = @Posicao
            WHERE id = @Id",
            new
            {
                Id = id,
                request.IdProduto,
                request.IdPrateleira,
                request.IdFilial,
                request.Posicao
            });

        return Ok("Vínculo atualizado com sucesso.");
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Excluir(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            DELETE FROM ProdutoPrateleira
            WHERE id = @Id",
            new { Id = id });

        return Ok("Vínculo removido com sucesso.");
    }
}

public class ProdutoPrateleiraRequest
{
    public int IdProduto { get; set; }
    public int IdPrateleira { get; set; }
    public int IdFilial { get; set; }
    public string? Posicao { get; set; }
}