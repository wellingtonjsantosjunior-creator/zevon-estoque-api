using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProdutosController : ControllerBase
{
    private readonly string _connectionString;

    public ProdutosController(string connectionString)
    {
        _connectionString = connectionString;
    }

    private int GetIdEmpresa() =>
        int.TryParse(User.FindFirst("id_empresa")?.Value, out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] int? idFilial)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        var produtos = await conn.QueryAsync(@"
            SELECT
                p.id_produto AS idProduto,
                p.id_categoria AS idCategoria,
                c.nome AS categoria,
                p.id_fornecedor AS idFornecedor,
                f.nome AS fornecedor,
                p.id_filial AS idFilial,
                p.nome,
                p.codigo_sku AS codigoSku,
                p.codigo_sku AS sku,
                p.descricao,
                p.preco_custo AS precoCusto,
                p.preco_venda AS precoVenda,
                p.unidade,
                p.ativo,
                p.criado_em AS criadoEm,
                ef.qtd_atual AS saldo,
                ef.qtd_minima AS estoqueMinimo,
                (SELECT codigo_barras FROM Etiquetas
 WHERE id_produto = p.id_produto AND ativo = true
 LIMIT 1) AS codigoEtiqueta
            FROM Produtos p
            LEFT JOIN Categorias c ON p.id_categoria = c.id_categoria
            LEFT JOIN Fornecedores f ON p.id_fornecedor = f.id_fornecedor
            LEFT JOIN EstoqueFilial ef ON ef.id_produto = p.id_produto
                AND ef.id_filial = @IdFilial
            WHERE p.ativo = true
              AND p.IdEmpresa = @IdEmpresa
              AND (@IdFilial IS NULL OR p.id_filial = @IdFilial)
            ORDER BY p.nome",
            new { IdFilial = idFilial, IdEmpresa = idEmpresa });

        return Ok(produtos);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> BuscarPorId(int id)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        var produto = await conn.QueryFirstOrDefaultAsync(@"
            SELECT
                p.id_produto AS idProduto,
                p.id_categoria AS idCategoria,
                p.id_fornecedor AS idFornecedor,
                p.id_filial AS idFilial,
                p.nome,
                p.codigo_sku AS codigoSku,
                p.codigo_sku AS sku,
                p.descricao,
                p.preco_custo AS precoCusto,
                p.preco_venda AS precoVenda,
                p.unidade,
                p.ativo,
                p.criado_em AS criadoEm,
                (SELECT codigo_barras FROM Etiquetas
 WHERE id_produto = p.id_produto AND ativo = true
 LIMIT 1) AS codigoEtiqueta
            FROM Produtos p
            WHERE p.id_produto = @Id
              AND p.IdEmpresa = @IdEmpresa",
            new { Id = id, IdEmpresa = idEmpresa });

        if (produto == null)
            return NotFound("Produto nao encontrado.");

        return Ok(produto);
    }

    [HttpPut("{id}/estoque-minimo")]
    public async Task<IActionResult> AtualizarEstoqueMinimo(
        int id, [FromBody] AtualizarEstoqueMinimoRequest request)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE EstoqueFilial
            SET qtd_minima = @QtdMinima
            WHERE id_produto = @IdProduto
              AND id_filial = @IdFilial",
            new
            {
                request.QtdMinima,
                IdProduto = id,
                request.IdFilial
            });

        await conn.ExecuteAsync(@"
            UPDATE Produtos
            SET preco_custo = @PrecoCusto
            WHERE id_produto = @IdProduto
              AND IdEmpresa = @IdEmpresa",
            new
            {
                request.PrecoCusto,
                IdProduto = id,
                IdEmpresa = idEmpresa
            });

        return Ok("Estoque mínimo e preço atualizados.");
    }

    public class AtualizarEstoqueMinimoRequest
    {
        public int IdFilial { get; set; }
        public int QtdMinima { get; set; }
        public decimal? PrecoCusto { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] ProdutoRequest produto)
    {
        if (string.IsNullOrWhiteSpace(produto.Nome))
            return BadRequest("Nome do produto e obrigatorio.");

        if (produto.IdFilial == null || produto.IdFilial == 0)
            return BadRequest("Filial e obrigatoria para gerar estoque e etiqueta.");

        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        // Gera SKU único baseado no ID do produto ou timestamp
var prefixo = "PROD";
var sku = $"{prefixo}{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}";


           // Gera etiqueta com prefixo ETQ
var codigoEtiqueta = $"ETQ-{sku}";

        try
        {
            // Insere produto
            var idProduto = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO Produtos
                    (id_categoria, id_fornecedor, id_filial, nome, codigo_sku,
                     descricao, preco_custo, preco_venda, unidade, ativo, criado_em, IdEmpresa)
                VALUES
                    (@IdCategoria, @IdFornecedor, @IdFilial, @Nome, @Sku,
                     @Descricao, @PrecoCusto, @PrecoVenda, @Unidade, true, NOW(), @IdEmpresa);
                SELECT SCOPE_IDENTITY();",
                new
                {
                    produto.IdCategoria,
                    produto.IdFornecedor,
                    produto.IdFilial,
                    produto.Nome,
                    Sku = sku,
                    produto.Descricao,
                    produto.PrecoCusto,
                    produto.PrecoVenda,
                    produto.Unidade,
                    IdEmpresa = idEmpresa
                });

            // Cria registro de estoque na filial com saldo 0
            await conn.ExecuteAsync(@"
                IF NOT EXISTS (SELECT 1 FROM EstoqueFilial WHERE id_produto = @IdProduto AND id_filial = @IdFilial)
               INSERT INTO EstoqueFilial (id_produto, id_filial, qtd_atual, qtd_minima)
VALUES (@IdProduto, @IdFilial, 0, 0)
ON CONFLICT (id_produto, id_filial) DO NOTHING;,
                new { IdProduto = idProduto, produto.IdFilial });


await conn.ExecuteAsync(@"
    INSERT INTO Etiquetas (id_produto, id_filial, codigo_barras, ativo)
    VALUES (@IdProduto, @IdFilial, @CodigoBarras, true)",
    new
    {
        IdProduto = idProduto,
        produto.IdFilial,
        CodigoBarras = codigoEtiqueta
    });

            return Ok(new
{
    idProduto,
    codigoSku = sku,
    codigoEtiqueta = codigoEtiqueta,
    mensagem = "Produto cadastrado com sucesso."
});
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { erro = ex.Message, detalhe = ex.InnerException?.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Atualizar(int id, [FromBody] ProdutoRequest produto)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Produtos
            SET id_categoria = @IdCategoria,
                id_fornecedor = @IdFornecedor,
                nome = @Nome,
                descricao = @Descricao,
                preco_custo = @PrecoCusto,
                preco_venda = @PrecoVenda,
                unidade = @Unidade
            WHERE id_produto = @Id
              AND IdEmpresa = @IdEmpresa",
            new
            {
                Id = id,
                produto.IdCategoria,
                produto.IdFornecedor,
                produto.Nome,
                produto.Descricao,
                produto.PrecoCusto,
                produto.PrecoVenda,
                produto.Unidade,
                IdEmpresa = idEmpresa
            });

        return Ok("Produto atualizado com sucesso.");
    }

    [HttpGet("produto/{idProduto}")]
    public async Task<IActionResult> GetPorProduto(
        int idProduto, [FromQuery] int? idFilial)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        var result = await conn.QueryAsync(@"
            SELECT
                pr.id_prateleira AS idPrateleira,
                pr.descricao,
                pr.codigo_barras AS codigoBarras,
                pr.id_filial AS idFilial
            FROM ProdutoPrateleira pp
            INNER JOIN Prateleiras pr ON pr.id_prateleira = pp.id_prateleira
            INNER JOIN Produtos p ON p.id_produto = pp.id_produto
            WHERE pp.id_produto = @IdProduto
              AND p.IdEmpresa = @IdEmpresa
              AND (@IdFilial IS NULL OR pr.id_filial = @IdFilial)",
            new { IdProduto = idProduto, IdFilial = idFilial, IdEmpresa = idEmpresa });

        return Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Inativar(int id)
    {
        var idEmpresa = GetIdEmpresa();
        using var conn = new NpgsqlConnection(_connectionString);

        await conn.ExecuteAsync(@"
            UPDATE Produtos SET ativo = false
            WHERE id_produto = @Id
              AND IdEmpresa = @IdEmpresa",
            new { Id = id, IdEmpresa = idEmpresa });

        return Ok("Produto inativado com sucesso.");
    }
}

public class ProdutoRequest
{
    public int? IdCategoria { get; set; }
    public int? IdFornecedor { get; set; }
    public int? IdFilial { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string? CodigoSku { get; set; }
    public string? Descricao { get; set; }
    public decimal? PrecoCusto { get; set; }
    public decimal? PrecoVenda { get; set; }
    public string Unidade { get; set; } = "UN";
}