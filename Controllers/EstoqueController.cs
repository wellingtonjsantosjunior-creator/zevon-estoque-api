using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using ZevonEstoque.Models;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EstoqueController : ControllerBase
{
    private readonly string _connectionString;

    public EstoqueController(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<bool> FilialEmInventario(NpgsqlConnection conn, int idFilial)
    {
        var inventario = await conn.QueryFirstOrDefaultAsync(@"
            SELECT id_inventario FROM Inventarios
            WHERE id_filial = @IdFilial
              AND status = 'EM_ANDAMENTO'",
            new { IdFilial = idFilial });
        return inventario != null;
    }

    [HttpPost("entrada")]
    public async Task<IActionResult> Entrada([FromBody] EntradaRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        if (await FilialEmInventario(conn, request.IdFilial))
            return BadRequest("INVENTARIO_EM_ANDAMENTO");

        try
        {
            var resultado = await conn.QueryFirstOrDefaultAsync(
                "SELECT * FROM sp_entrada_estoque(@id_produto, @id_filial, @id_prateleira, @id_usuario, @quantidade, @observacao)",
                new
                {
                    id_produto = request.IdProduto,
                    id_filial = request.IdFilial,
                    id_prateleira = request.IdPrateleira,
                    id_usuario = request.IdUsuario,
                    quantidade = request.Quantidade,
                    observacao = request.Observacao
                });

            return Ok(resultado);
        }
        catch (PostgresException ex) when (ex.SqlState == "P0001")
        {
            // Regra de negócio levantada pela função (RAISE EXCEPTION)
            return BadRequest(ex.MessageText);
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            // FK inválida (produto/filial/prateleira/usuário inexistente)
            return BadRequest($"Referência inválida: {ex.ConstraintName}");
        }
    }

    [HttpPost("saida")]
    public async Task<IActionResult> Saida([FromBody] SaidaRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var prateleira = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
            SELECT id_filial FROM Prateleiras
            WHERE codigo_barras = @CodigoPrateleira",
            new { request.CodigoPrateleira });

        if (prateleira != null && await FilialEmInventario(conn, (int)prateleira.id_filial))
            return BadRequest("INVENTARIO_EM_ANDAMENTO");

        try
        {
            var resultado = await conn.QueryFirstOrDefaultAsync(
                "SELECT * FROM sp_saida_por_prateleira(@codigo_prateleira, @id_produto, @id_usuario, @quantidade, @observacao, @id_requisicao)",
                new
                {
                    codigo_prateleira = request.CodigoPrateleira,
                    id_produto = request.IdProduto,
                    id_usuario = request.IdUsuario,
                    quantidade = request.Quantidade,
                    observacao = request.Observacao,
                    id_requisicao = request.IdRequisicao
                });

            return Ok(resultado);
        }
        catch (PostgresException ex) when (ex.SqlState == "P0001")
        {
            // SALDO_INSUFICIENTE / PRATELEIRA_NAO_ENCONTRADA / QUANTIDADE_INVALIDA
            return BadRequest(ex.MessageText);
        }
        catch (PostgresException ex) when (ex.SqlState == "23503")
        {
            // FK inválida (produto/filial/prateleira/usuário inexistente)
            return BadRequest($"Referência inválida: {ex.ConstraintName}");
        }
    }

    // ── RECALCULAR MÍNIMO/MÁXIMO A PARTIR DO CONSUMO ──────────────
    // Política min-max: mínimo = consumo médio mensal (ponto de reposição),
    // máximo = 2x o consumo médio mensal (nível alvo após reposição).
    // Só atualiza produtos com saída registrada no período; os demais mantêm
    // o valor atual (sem histórico não há base para recalcular).
    [HttpPost("recalcular-minimos/{idFilial}")]
    public async Task<IActionResult> RecalcularMinimos(
        int idFilial, [FromQuery] int meses = 3)
    {
        if (meses <= 0) return BadRequest("Informe um número de meses válido.");

        using var conn = new NpgsqlConnection(_connectionString);

        var atualizados = await conn.QueryAsync(@"
            WITH consumo AS (
                SELECT m.id_produto, SUM(m.quantidade) AS total_saida
                FROM Movimentacoes m
                WHERE m.id_filial = @IdFilial
                  AND m.tipo = 'SAIDA'
                  AND m.data_hora >= NOW() - make_interval(months => @Meses)
                GROUP BY m.id_produto
            )
            UPDATE EstoqueFilial ef
            SET qtd_minima = CEIL(c.total_saida / @Meses::numeric)::int,
                qtd_maxima = CEIL(c.total_saida / @Meses::numeric * 2)::int
            FROM consumo c
            WHERE ef.id_produto = c.id_produto
              AND ef.id_filial = @IdFilial
            RETURNING
                ef.id_produto AS ""idProduto"",
                ef.qtd_minima AS ""novoMinimo"",
                ef.qtd_maxima AS ""novoMaximo""",
            new { IdFilial = idFilial, Meses = meses });

        var lista = atualizados.ToList();
        return Ok(new
        {
            produtosAtualizados = lista.Count,
            mesesConsiderados = meses,
            detalhes = lista
        });
    }

    [HttpGet("kardex")]
    public async Task<IActionResult> Kardex([FromQuery] KardexFiltro filtro)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var resultado = await conn.QueryAsync(
            "SELECT * FROM sp_kardex(@id_filial, @id_produto, @data_inicio, @data_fim)",
            new
            {
                id_filial = filtro.IdFilial,
                id_produto = filtro.IdProduto,
                data_inicio = filtro.DataInicio,
                data_fim = filtro.DataFim
            });

        return Ok(resultado);
    }

    [HttpGet("saldo/{idFilial}")]
    public async Task<IActionResult> Saldo(int idFilial)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var resultado = await conn.QueryAsync(@"
            SELECT 
                p.id_produto AS ""idProduto"",
                p.nome AS produto,
                p.codigo_sku AS ""codigoSku"",
                p.codigo_sku AS sku,
                ef.qtd_atual AS saldo,
                ef.qtd_minima AS minimo,
                CASE WHEN ef.qtd_atual <= ef.qtd_minima 
                     THEN 'ALERTA' ELSE 'OK' END AS status
            FROM EstoqueFilial ef
            INNER JOIN Produtos p ON ef.id_produto = p.id_produto
            WHERE ef.id_filial = @IdFilial
              AND p.ativo = true
            ORDER BY status DESC, p.nome",
            new { IdFilial = idFilial });

        return Ok(resultado);
    }

    [HttpGet("etiqueta/{codigoBarras}")]
public async Task<IActionResult> BuscarEtiqueta(string codigoBarras)
{
    using var conn = new NpgsqlConnection(_connectionString);

    var resultado = await conn.QueryFirstOrDefaultAsync(@"
        SELECT
            e.codigo_barras AS ""codigoEtiqueta"",
            p.id_produto AS ""idProduto"",
            p.nome AS produto,
            p.codigo_sku AS ""codigoSku"",
            p.codigo_sku AS sku,
            ef.qtd_atual AS saldo,
            f.id_filial AS ""idFilial"",
            f.nome AS filial,
            pr.id_prateleira AS ""idPrateleira"",
            pr.codigo_barras AS ""codigoPrateleira"",
            pr.descricao AS prateleira,
            pp.posicao
        FROM Etiquetas e
        INNER JOIN Produtos p ON e.id_produto = p.id_produto
        INNER JOIN EstoqueFilial ef 
            ON e.id_produto = ef.id_produto 
           AND e.id_filial = ef.id_filial
        INNER JOIN Filiais f ON e.id_filial = f.id_filial
        INNER JOIN ProdutoPrateleira pp 
            ON p.id_produto = pp.id_produto 
           AND e.id_filial = pp.id_filial
        INNER JOIN Prateleiras pr ON pp.id_prateleira = pr.id_prateleira
        WHERE e.codigo_barras = @CodigoBarras 
          AND e.ativo = true
          AND p.ativo = true
        LIMIT 1",
        new { CodigoBarras = codigoBarras });

    if (resultado == null)
        return NotFound("Etiqueta não encontrada.");

    return Ok(resultado);
}


    [HttpGet("prateleira/{codigoBarras}")]
    public async Task<IActionResult> BuscarPrateleira(string codigoBarras)
    {
        using var conn =new NpgsqlConnection(_connectionString);

        var resultado = await conn.QueryAsync(@"
            SELECT 
                p.id_produto AS ""idProduto"",
                p.nome AS produto,
                p.codigo_sku AS ""codigoSku"",
                p.codigo_sku AS sku,
                ef.qtd_atual AS saldo,
                pp.posicao,
                pr.codigo_barras AS ""codigoPrateleira"",
                pr.descricao AS prateleira
            FROM Prateleiras pr
            INNER JOIN ProdutoPrateleira pp ON pr.id_prateleira = pp.id_prateleira
            INNER JOIN Produtos p ON pp.id_produto = p.id_produto
            INNER JOIN EstoqueFilial ef 
                ON p.id_produto = ef.id_produto 
               AND pr.id_filial = ef.id_filial
            WHERE pr.codigo_barras = @CodigoBarras 
              AND pr.ativo = true
              AND p.ativo = true",
            new { CodigoBarras = codigoBarras });

        if (!resultado.Any())
            return NotFound("Prateleira não encontrada ou vazia.");

        return Ok(resultado);
    }
}