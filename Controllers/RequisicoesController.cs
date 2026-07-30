using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Dapper;
using FirebaseAdmin.Messaging;

namespace ZevonEstoque.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RequisicoesController : ControllerBase
{
    private readonly string _connectionString;

    public RequisicoesController(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── LISTAR AGRUPADO ──────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] int? idFilial,
        [FromQuery] string? status,
        [FromQuery] int? idUsuario)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        // Busca todos os itens
        var itens = await conn.QueryAsync(@"
            SELECT
                r.id_requisicao AS ""idRequisicao"",
                r.id_grupo AS ""idGrupo"",
                r.id_filial AS ""idFilial"",
                f.nome AS filial,
                r.id_usuario_solicitante AS ""idUsuarioSolicitante"",
                us.nome AS ""nomeSolicitante"",
                r.id_produto AS ""idProduto"",
                p.nome AS produto,
                p.codigo_sku AS sku,
                p.unidade,
                r.quantidade,
                r.justificativa,
                r.status,
                r.data_retirada_prevista AS ""dataRetiradaPrevista"",
                r.id_usuario_atendente AS ""idUsuarioAtendente"",
                ua.nome AS ""nomeAtendente"",
                r.observacao_atendente AS ""observacaoAtendente"",
                r.valor_unitario AS ""valorUnitario"",
                r.valor_total AS ""valorTotal"",
                r.criado_em AS ""criadoEm"",
                r.atendido_em AS ""atendidoEm""
            FROM Requisicoes r
            INNER JOIN Filiais f ON r.id_filial = f.id_filial
            INNER JOIN Usuarios us ON r.id_usuario_solicitante = us.id_usuario
            INNER JOIN Produtos p ON r.id_produto = p.id_produto
            LEFT JOIN Usuarios ua ON r.id_usuario_atendente = ua.id_usuario
            WHERE (@IdFilial IS NULL OR r.id_filial = @IdFilial)
              AND (@Status IS NULL OR r.status = @Status)
              AND (@IdUsuario IS NULL OR r.id_usuario_solicitante = @IdUsuario)
            ORDER BY r.criado_em DESC",
            new { IdFilial = idFilial, Status = status, IdUsuario = idUsuario });

        // Agrupa por id_grupo
        var grupos = itens
            .GroupBy(r => (string)r.idGrupo?.ToString() ?? r.idRequisicao.ToString())
            .Select(g =>
            {
                var primeiro = g.First();
                var todosItens = g.ToList();
                return new
                {
                    idGrupo = primeiro.idGrupo,
                    idRequisicao = primeiro.idRequisicao, // mantém para compatibilidade
                    idFilial = primeiro.idFilial,
                    filial = primeiro.filial,
                    idUsuarioSolicitante = primeiro.idUsuarioSolicitante,
                    nomeSolicitante = primeiro.nomeSolicitante,
                    justificativa = primeiro.justificativa,
                    status = primeiro.status,
                    dataRetiradaPrevista = primeiro.dataRetiradaPrevista,
                    idUsuarioAtendente = primeiro.idUsuarioAtendente,
                    nomeAtendente = primeiro.nomeAtendente,
                    observacaoAtendente = primeiro.observacaoAtendente,
                    criadoEm = primeiro.criadoEm,
                    atendidoEm = primeiro.atendidoEm,
                    totalItens = todosItens.Count,
                    valorTotal = todosItens.Sum(i => (decimal)i.valorTotal),
                    itens = todosItens.Select(i => new
                    {
                        idRequisicao = i.idRequisicao,
                        idProduto = i.idProduto,
                        produto = i.produto,
                        sku = i.sku,
                        unidade = i.unidade,
                        quantidade = i.quantidade,
                        status = i.status,
                        valorUnitario = i.valorUnitario,
                        valorTotal = i.valorTotal,
                    }).ToList()
                };
            })
            .ToList();

        return Ok(grupos);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> BuscarPorId(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        var requisicao = await conn.QueryFirstOrDefaultAsync(@"
            SELECT
                r.id_requisicao AS ""idRequisicao"",
                r.id_grupo AS ""idGrupo"",
                r.id_filial AS ""idFilial"",
                f.nome AS filial,
                r.id_usuario_solicitante AS ""idUsuarioSolicitante"",
                us.nome AS ""nomeSolicitante"",
                r.id_produto AS ""idProduto"",
                p.nome AS produto,
                p.codigo_sku AS sku,
                p.unidade,
                r.quantidade,
                r.justificativa,
                r.status,
                r.data_retirada_prevista AS ""dataRetiradaPrevista"",
                r.id_usuario_atendente AS ""idUsuarioAtendente"",
                ua.nome AS ""nomeAtendente"",
                r.observacao_atendente AS ""observacaoAtendente"",
                r.valor_unitario AS ""valorUnitario"",
                r.valor_total AS ""valorTotal"",
                r.criado_em AS ""criadoEm"",
                r.atendido_em AS ""atendidoEm""
            FROM Requisicoes r
            INNER JOIN Filiais f ON r.id_filial = f.id_filial
            INNER JOIN Usuarios us ON r.id_usuario_solicitante = us.id_usuario
            INNER JOIN Produtos p ON r.id_produto = p.id_produto
            LEFT JOIN Usuarios ua ON r.id_usuario_atendente = ua.id_usuario
            WHERE r.id_requisicao = @Id",
            new { Id = id });
        if (requisicao == null) return NotFound("Requisicao nao encontrada.");
        return Ok(requisicao);
    }

    // ── CRIAR COM GRUPO ──────────────────────────────────────────
    [HttpPost]
public async Task<IActionResult> Criar([FromBody] RequisicaoRequest request)
{
    if (request.IdProduto == 0) return BadRequest("Produto e obrigatorio.");
    if (request.Quantidade <= 0) return BadRequest("Quantidade invalida.");

    using var conn = new NpgsqlConnection(_connectionString);

    // Converte idGrupo string para Guid ou gera novo
    Guid idGrupo;
    if (!string.IsNullOrEmpty(request.IdGrupo) && Guid.TryParse(request.IdGrupo, out var guidParsed))
        idGrupo = guidParsed;
    else
        idGrupo = Guid.NewGuid();

    var estoque = await conn.QueryFirstOrDefaultAsync(@"
        SELECT ef.qtd_atual, p.preco_custo
        FROM EstoqueFilial ef
        INNER JOIN Produtos p ON ef.id_produto = p.id_produto
        WHERE ef.id_produto = @IdProduto
          AND ef.id_filial = @IdFilial",
        new { request.IdProduto, request.IdFilial });

    decimal valorUnitario = estoque?.preco_custo ?? 0;
    decimal valorTotal = valorUnitario * request.Quantidade;

    var id = await conn.QueryFirstAsync<int>(@"
        INSERT INTO Requisicoes
        (id_filial, id_usuario_solicitante, id_produto, quantidade,
         justificativa, status, data_retirada_prevista,
         valor_unitario, valor_total, id_grupo)
        VALUES
        (@IdFilial, @IdUsuarioSolicitante, @IdProduto, @Quantidade,
         @Justificativa, 'PENDENTE', @DataRetiradaPrevista,
         @ValorUnitario, @ValorTotal, @IdGrupo)
        RETURNING id_requisicao;",
        new
        {
            request.IdFilial,
            request.IdUsuarioSolicitante,
            request.IdProduto,
            request.Quantidade,
            request.Justificativa,
            request.DataRetiradaPrevista,
            ValorUnitario = valorUnitario,
            ValorTotal = valorTotal,
            IdGrupo = idGrupo
        });

    if (request.PrimeiroDoGrupo)
    {
        await NotificarOperadores(conn, request.IdFilial,
            "Nova Requisicao",
            $"Nova solicitacao de material recebida!");
    }

    return Ok(new
    {
        idRequisicao = id,
        idGrupo = idGrupo.ToString(),
        mensagem = "Requisicao criada com sucesso."
    });
}

    // ── ATUALIZAR STATUS DE UM ITEM ───────────────────────────────
    [HttpPut("{id}/status")]
    public async Task<IActionResult> AtualizarStatus(
        int id,
        [FromBody] AtualizarStatusRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        var requisicao = await conn.QueryFirstOrDefaultAsync(@"
            SELECT r.*, u.fcm_token AS ""fcmTokenSolicitante"",
                   r.id_usuario_solicitante AS ""idUsuarioSolicitante""
            FROM Requisicoes r
            INNER JOIN Usuarios u ON r.id_usuario_solicitante = u.id_usuario
            WHERE r.id_requisicao = @Id",
            new { Id = id });

        if (requisicao == null) return NotFound("Requisicao nao encontrada.");

        await conn.ExecuteAsync(@"
            UPDATE Requisicoes
            SET status = @Status,
                id_usuario_atendente = @IdAtendente,
                observacao_atendente = @Observacao
            WHERE id_requisicao = @Id",
            new
            {
                Status = request.Status,
                IdAtendente = request.IdUsuarioAtendente,
                Observacao = request.Observacao,
                Id = id
            });

        await _NotificarMudancaStatus(conn, id, request, requisicao);

        return Ok("Status atualizado com sucesso.");
    }

    // ── ATUALIZAR STATUS DO GRUPO INTEIRO ─────────────────────────
    [HttpPut("grupo/{idGrupo}/status")]
    public async Task<IActionResult> AtualizarStatusGrupo(
        string idGrupo,
        [FromBody] AtualizarStatusRequest request)
    {
        using var conn = new NpgsqlConnection(_connectionString);

        // Busca todos os itens do grupo
        var itens = await conn.QueryAsync(@"
            SELECT r.id_requisicao AS ""idRequisicao"",
                   r.id_usuario_solicitante AS ""idUsuarioSolicitante"",
                   u.fcm_token AS ""fcmTokenSolicitante""
            FROM Requisicoes r
            INNER JOIN Usuarios u ON r.id_usuario_solicitante = u.id_usuario
            WHERE r.id_grupo = @IdGrupo",
            new { IdGrupo = idGrupo });

        if (!itens.Any()) return NotFound("Grupo nao encontrado.");

        // Atualiza todos os itens do grupo
        await conn.ExecuteAsync(@"
            UPDATE Requisicoes
            SET status = @Status,
                id_usuario_atendente = @IdAtendente,
                observacao_atendente = @Observacao
            WHERE id_grupo = @IdGrupo",
            new
            {
                Status = request.Status,
                IdAtendente = request.IdUsuarioAtendente,
                Observacao = request.Observacao,
                IdGrupo = idGrupo
            });

        // Notifica solicitante uma vez
        var primeiro = itens.First();
        int idSolicitante = (int)primeiro.idUsuarioSolicitante;

        var mensagens = new Dictionary<string, string>
        {
            ["EM_SEPARACAO"] = "Seu pedido esta em separacao!",
            ["SEPARADO"]     = "Seu pedido esta pronto para retirada!",
            ["ENTREGUE"]     = "Seu pedido foi entregue!",
            ["REJEITADO"]    = $"Seu pedido foi rejeitado. {request.Observacao ?? ""}"
        };

        if (mensagens.TryGetValue(request.Status, out var msg))
        {
            await SalvarNotificacaoBanco(conn, idSolicitante,
                $"Requisicao Grupo", msg);

            if (primeiro.fcmTokenSolicitante != null)
                await EnviarNotificacao(
                    (string)primeiro.fcmTokenSolicitante,
                    "Requisicao", msg);
        }

        return Ok("Status do grupo atualizado com sucesso.");
    }

    // ── HELPERS ───────────────────────────────────────────────────

    private async Task _NotificarMudancaStatus(
        NpgsqlConnection conn, int id,
        AtualizarStatusRequest request,
        dynamic requisicao)
    {
        var mensagens = new Dictionary<string, string>
        {
            ["EM_SEPARACAO"] = "Seu pedido esta em separacao!",
            ["SEPARADO"]     = "Seu pedido esta pronto para retirada!",
            ["ENTREGUE"]     = "Seu pedido foi entregue!",
            ["REJEITADO"]    = $"Seu pedido foi rejeitado. {request.Observacao ?? ""}"
        };

        if (mensagens.TryGetValue(request.Status, out var msg))
        {
            int idSolicitante = (int)requisicao.idUsuarioSolicitante;
            await SalvarNotificacaoBanco(conn, idSolicitante,
                $"Requisicao #{id}", msg);

            if (requisicao.fcmTokenSolicitante != null)
                await EnviarNotificacao(
                    (string)requisicao.fcmTokenSolicitante,
                    $"Requisicao #{id}", msg);
        }
    }

    private async Task NotificarOperadores(
        NpgsqlConnection conn, int idFilial, string titulo, string corpo)
    {
        var operadores = await conn.QueryAsync<dynamic>(@"
            SELECT id_usuario AS ""idUsuario"", fcm_token AS ""fcmToken""
            FROM Usuarios
            WHERE id_filial = @IdFilial
              AND perfil IN ('ADMIN', 'OPERADOR')
              AND ativo = true",
            new { IdFilial = idFilial });

        foreach (var op in operadores)
        {
            await SalvarNotificacaoBanco(conn, (int)op.idUsuario, titulo, corpo);
            if (op.fcmToken != null)
                await EnviarNotificacao((string)op.fcmToken, titulo, corpo);
        }
    }

    private async Task SalvarNotificacaoBanco(
        NpgsqlConnection conn, int idUsuario, string titulo, string corpo)
    {
        await conn.ExecuteAsync(@"
            INSERT INTO Notificacoes (id_usuario, titulo, corpo)
            VALUES (@IdUsuario, @Titulo, @Corpo)",
            new { IdUsuario = idUsuario, Titulo = titulo, Corpo = corpo });
    }

    private async Task EnviarNotificacao(
        string token, string titulo, string corpo)
    {
        try
        {
            var message = new Message
            {
                Token = token,
                Notification = new Notification { Title = titulo, Body = corpo },
                Android = new AndroidConfig { Priority = Priority.High }
            };
            await FirebaseMessaging.DefaultInstance.SendAsync(message);
        }
        catch { }
    }
}

public class RequisicaoRequest
{
    public int IdFilial { get; set; }
    public int IdUsuarioSolicitante { get; set; }
    public int IdProduto { get; set; }
    public int Quantidade { get; set; }
    public string? Justificativa { get; set; }
    public DateTime? DataRetiradaPrevista { get; set; }
    public string? IdGrupo { get; set; }
    public bool PrimeiroDoGrupo { get; set; } = true;
}

public class AtualizarStatusRequest
{
    public string Status { get; set; } = string.Empty;
    public int? IdUsuarioAtendente { get; set; }
    public string? Observacao { get; set; }
}