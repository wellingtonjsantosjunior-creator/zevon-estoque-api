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

        var resultado = await conn.QueryFirstOrDefaultAsync(
            "EXEC sp_EntradaEstoque @id_produto, @id_filial, @id_prateleira, @id_usuario, @quantidade, @observacao",
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

        var resultado = await conn.QueryFirstOrDefaultAsync(
            "EXEC sp_SaidaPorPrateleira @codigo_prateleira, @id_produto, @id_usuario, @quantidade, @observacao, @id_requisicao",
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

    [HttpGet("kardex")]
    public async Task<IActionResult> Kardex([FromQuery] KardexFiltro filtro)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var resultado = await conn.QueryAsync(
            "EXEC sp_Kardex @id_filial, @id_produto, @data_inicio, @data_fim",
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
                p.id_produto AS idProduto,
                p.nome AS produto,
                p.codigo_sku AS codigoSku,
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
        SELECT TOP 1
            e.codigo_barras AS codigoEtiqueta,
            p.id_produto AS idProduto,
            p.nome AS produto,
            p.codigo_sku AS codigoSku,
            p.codigo_sku AS sku,
            ef.qtd_atual AS saldo,
            f.id_filial AS idFilial,
            f.nome AS filial,
            pr.id_prateleira AS idPrateleira,
            pr.codigo_barras AS codigoPrateleira,
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
          AND p.ativo = true",
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
                p.id_produto AS idProduto,
                p.nome AS produto,
                p.codigo_sku AS codigoSku,
                p.codigo_sku AS sku,
                ef.qtd_atual AS saldo,
                pp.posicao,
                pr.codigo_barras AS codigoPrateleira,
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