namespace ZevonEstoque.Models;

public class Usuario
{
    public int IdUsuario { get; set; }
    public int? IdFilial { get; set; }
    public int IdEmpresa { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string SenhaHash { get; set; } = string.Empty;
    public string Perfil { get; set; } = "OPERADOR";
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
    public bool PrimeiroAcesso { get; set; }
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Senha { get; set; } = string.Empty;
}

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public int IdUsuario { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Perfil { get; set; } = string.Empty;
    public int IdFilial { get; set; }
    public int IdEmpresa { get; set; }
    public bool PrimeiroAcesso { get; set; }
}

public class RedefinirSenhaRequest
{
    public int IdUsuario { get; set; }
    public string NovaSenha { get; set; } = string.Empty;
}