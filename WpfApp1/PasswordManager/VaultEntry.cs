using System;
using System.Collections.Generic;
using System.Text;

namespace WpfApp1.PasswordManager;
public class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Website { get; set; } = "";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string Notes { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
