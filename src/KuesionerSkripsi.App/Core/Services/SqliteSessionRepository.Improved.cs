using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.Data.Sqlite;
using KuesionerSkripsi.Core.Models;

namespace KuesionerSkripsi.Core.Services;

/// <summary>
/// Database lokal SQLite dengan improved error handling
/// Bersama untuk dua jalur kuesioner dengan retry logic dan better diagnostics
/// </summary>
public sealed class SqliteSessionRepositoryImproved
{
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 100;
    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// Menyimpan atau update sesi responden dengan retry logic
    /// </summary>
    public void UpsertCompletedSession(ResearchSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateCompletedSession(session);
        AppStoragePaths.EnsureDirectories();

        int retryCount = 0;
        Exception? lastException = null;

        while (retryCount < MaxRetries)
        {
            try
            {
                using var connection = CreateConnection();
                connection.Open();
                EnsureSchema(connection);

                using var command = connection.CreateCommand();
                command.CommandTimeout = CommandTimeoutSeconds;
                command.CommandText = GetUpsertCommandText();

                var respondent = session.Respondent;
                var pre = session.PreAssessment!;
                var post = session.PostAssessment!;

                AddParameters(command, session, respondent, pre, post);
                command.ExecuteNonQuery();

                LoggerService.LogInfo($"Session saved: {session.SessionId}");
                return; // Sukses
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && retryCount < MaxRetries - 1)
            {
                // Error code 5 = database locked, coba lagi
                lastException = ex;
                retryCount++;
                LoggerService.LogWarning($"Database locked, retry {retryCount}/{MaxRetries}");
                Thread.Sleep(RetryDelayMs * retryCount); // Exponential backoff
            }
            catch (SqliteException ex)
            {
                LoggerService.LogError($"SQLite error (code {ex.SqliteErrorCode}): {ex.Message}");
                throw new InvalidOperationException(
                    $"Gagal menyimpan data responden ke database. SQLite Error {ex.SqliteErrorCode}: {ex.Message}",
                    ex);
            }
            catch (Exception ex)
            {
                LoggerService.LogError("Database operation failed", ex);
                throw new InvalidOperationException(
                    $"Gagal menyimpan data responden ke database. Error: {ex.Message}",
                    ex);
            }
        }

        // Semua retry gagal
        LoggerService.LogError($"Database operation failed after {MaxRetries} retries", lastException);
        throw new InvalidOperationException(
            $"Gagal menyimpan data responden ke database setelah {MaxRetries} percobaan ulang. Database mungkin terkunci atau bermasalah.",
            lastException);
    }

    /// <summary>
    /// Membaca semua sesi yang sudah selesai dari database
    /// </summary>
    public IReadOnlyList<ResearchSession> GetAllCompletedSessions()
    {
        AppStoragePaths.EnsureDirectories();

        try
        {
            using var connection = CreateConnection();
            connection.Open();
            EnsureSchema(connection);

            using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = GetSelectCommandText();

            using var reader = command.ExecuteReader();
            var sessions = new List<ResearchSession>();
            while (reader.Read())
            {
                sessions.Add(ReadSession(reader));
            }

            LoggerService.LogInfo($"Loaded {sessions.Count} sessions from database");
            return sessions;
        }
        catch (Exception ex)
        {
            LoggerService.LogError("Failed to read database", ex);
            throw new InvalidOperationException(
                $"Gagal membaca data dari database. Error: {ex.Message}",
                ex);
        }
    }

    private static SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = AppStoragePaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return new SqliteConnection(builder.ToString());
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
CREATE TABLE IF NOT EXISTS CompletedSessions (
    SessionId TEXT NOT NULL PRIMARY KEY,
    CreatedAt TEXT NOT NULL,
    QuestionnaireDate TEXT NOT NULL,
    MedicalRecordNumber TEXT,
    FullName TEXT,
    BirthPlace TEXT,
    BirthDate TEXT NULL,
    AgeInYears INTEGER NULL,
    AgeDisplay TEXT,
    Gender TEXT,
    Education TEXT,
    OtherEducation TEXT,
    Address TEXT,
    ExtractionExperience TEXT,
    ConsentChoice TEXT,
    InterventionType TEXT NOT NULL DEFAULT 'Tidak Tercatat',
    SignaturePng BLOB NULL,
    PreQ1 INTEGER NOT NULL,
    PreQ2 INTEGER NOT NULL,
    PreQ3 INTEGER NOT NULL,
    PreQ4 INTEGER NOT NULL,
    PreQ5 INTEGER NOT NULL,
    PreTotal INTEGER NOT NULL,
    PreCategory INTEGER NOT NULL,
    PreCompletedAt TEXT NOT NULL,
    PostQ1 INTEGER NOT NULL,
    PostQ2 INTEGER NOT NULL,
    PostQ3 INTEGER NOT NULL,
    PostQ4 INTEGER NOT NULL,
    PostQ5 INTEGER NOT NULL,
    PostTotal INTEGER NOT NULL,
    PostCategory INTEGER NOT NULL,
    PostCompletedAt TEXT NOT NULL,
    CompletedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_CompletedSessions_CompletedAt ON CompletedSessions(CompletedAt);
";
            command.ExecuteNonQuery();
        }

        // Migrasi aman untuk database lama
        EnsureColumn(connection, "InterventionType", "TEXT NOT NULL DEFAULT 'Tidak Tercatat'");
    }

    private static void EnsureColumn(SqliteConnection connection, string columnName, string columnDefinition)
    {
        var exists = false;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(CompletedSessions);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
            return;

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE CompletedSessions ADD COLUMN {columnName} {columnDefinition};";
        try
        {
            alterCommand.ExecuteNonQuery();
            LoggerService.LogInfo($"Column added to database: {columnName}");
        }
        catch (Exception ex)
        {
            LoggerService.LogWarning($"Failed to add column {columnName}: {ex.Message}");
        }
    }

    private static ResearchSession ReadSession(SqliteDataReader reader)
    {
        var session = new ResearchSession(
            Guid.Parse(reader.GetString(0)),
            FromDbDate(reader.GetString(1)));

        var respondent = session.Respondent;
        respondent.QuestionnaireDate = FromDbDate(reader.GetString(2));
        respondent.MedicalRecordNumber = GetString(reader, 3);
        respondent.FullName = GetString(reader, 4);
        respondent.BirthPlace = GetString(reader, 5);
        if (!reader.IsDBNull(6))
        {
            respondent.BirthDate = FromDbDate(reader.GetString(6));
        }
        respondent.Gender = GetString(reader, 7);
        respondent.Education = GetString(reader, 8);
        respondent.OtherEducation = GetString(reader, 9);
        respondent.Address = GetString(reader, 10);
        respondent.ExtractionExperience = GetString(reader, 11);
        respondent.ConsentChoice = GetString(reader, 12);
        if (!reader.IsDBNull(13))
        {
            respondent.SetSignaturePng((byte[])reader[13]);
        }

        session.PreAssessment = new AnxietyAssessment
        {
            Answers = new[] { reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18) },
            TotalScore = reader.GetInt32(19),
            Category = (AnxietyCategory)reader.GetInt32(20),
            CompletedAt = FromDbDate(reader.GetString(21))
        };

        session.PostAssessment = new AnxietyAssessment
        {
            Answers = new[] { reader.GetInt32(22), reader.GetInt32(23), reader.GetInt32(24), reader.GetInt32(25), reader.GetInt32(26) },
            TotalScore = reader.GetInt32(27),
            Category = (AnxietyCategory)reader.GetInt32(28),
            CompletedAt = FromDbDate(reader.GetString(29))
        };

        session.InterventionType = GetString(reader, 30);
        return session;
    }

    private static void AddParameters(SqliteCommand command, ResearchSession session, Respondent respondent, AnxietyAssessment pre, AnxietyAssessment post)
    {
        Add(command, "$sessionId", session.SessionId.ToString("N"));
        Add(command, "$createdAt", ToDbDate(session.CreatedAt));
        Add(command, "$questionnaireDate", ToDbDate(respondent.QuestionnaireDate));
        Add(command, "$medicalRecordNumber", respondent.MedicalRecordNumber);
        Add(command, "$fullName", respondent.FullName);
        Add(command, "$birthPlace", respondent.BirthPlace);
        Add(command, "$birthDate", respondent.BirthDate.HasValue ? ToDbDate(respondent.BirthDate.Value) : null);
        Add(command, "$ageInYears", respondent.AgeInYears);
        Add(command, "$ageDisplay", respondent.AgeDisplay);
        Add(command, "$gender", respondent.Gender);
        Add(command, "$education", respondent.Education);
        Add(command, "$otherEducation", respondent.OtherEducation);
        Add(command, "$address", respondent.Address);
        Add(command, "$extractionExperience", respondent.ExtractionExperience);
        Add(command, "$consentChoice", respondent.ConsentChoice);
        Add(command, "$interventionType", session.InterventionType);
        Add(command, "$signaturePng", respondent.SignaturePng);
        Add(command, "$preQ1", pre.Answers[0]);
        Add(command, "$preQ2", pre.Answers[1]);
        Add(command, "$preQ3", pre.Answers[2]);
        Add(command, "$preQ4", pre.Answers[3]);
        Add(command, "$preQ5", pre.Answers[4]);
        Add(command, "$preTotal", pre.TotalScore);
        Add(command, "$preCategory", (int)pre.Category);
        Add(command, "$preCompletedAt", ToDbDate(pre.CompletedAt));
        Add(command, "$postQ1", post.Answers[0]);
        Add(command, "$postQ2", post.Answers[1]);
        Add(command, "$postQ3", post.Answers[2]);
        Add(command, "$postQ4", post.Answers[3]);
        Add(command, "$postQ5", post.Answers[4]);
        Add(command, "$postTotal", post.TotalScore);
        Add(command, "$postCategory", (int)post.Category);
        Add(command, "$postCompletedAt", ToDbDate(post.CompletedAt));
        Add(command, "$completedAt", ToDbDate(post.CompletedAt));
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string GetString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static string ToDbDate(DateTime value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime FromDbDate(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static void ValidateCompletedSession(ResearchSession session)
    {
        if (session.PreAssessment is null || session.PostAssessment is null)
        {
            throw new InvalidOperationException(
                "Data pre-test dan post-test belum lengkap untuk disimpan ke database.");
        }

        if (!InterventionTypes.IsKnown(session.InterventionType))
        {
            throw new InvalidOperationException(
                "Jenis kuesioner belum dipilih atau tidak valid.");
        }
    }

    private static string GetUpsertCommandText() => @"
INSERT INTO CompletedSessions (
    SessionId, CreatedAt, QuestionnaireDate, MedicalRecordNumber, FullName,
    BirthPlace, BirthDate, AgeInYears, AgeDisplay, Gender, Education,
    OtherEducation, Address, ExtractionExperience, ConsentChoice, InterventionType, SignaturePng,
    PreQ1, PreQ2, PreQ3, PreQ4, PreQ5, PreTotal, PreCategory, PreCompletedAt,
    PostQ1, PostQ2, PostQ3, PostQ4, PostQ5, PostTotal, PostCategory, PostCompletedAt,
    CompletedAt
)
VALUES (
    $sessionId, $createdAt, $questionnaireDate, $medicalRecordNumber, $fullName,
    $birthPlace, $birthDate, $ageInYears, $ageDisplay, $gender, $education,
    $otherEducation, $address, $extractionExperience, $consentChoice, $interventionType, $signaturePng,
    $preQ1, $preQ2, $preQ3, $preQ4, $preQ5, $preTotal, $preCategory, $preCompletedAt,
    $postQ1, $postQ2, $postQ3, $postQ4, $postQ5, $postTotal, $postCategory, $postCompletedAt,
    $completedAt
)
ON CONFLICT(SessionId) DO UPDATE SET
    CreatedAt = excluded.CreatedAt,
    QuestionnaireDate = excluded.QuestionnaireDate,
    MedicalRecordNumber = excluded.MedicalRecordNumber,
    FullName = excluded.FullName,
    BirthPlace = excluded.BirthPlace,
    BirthDate = excluded.BirthDate,
    AgeInYears = excluded.AgeInYears,
    AgeDisplay = excluded.AgeDisplay,
    Gender = excluded.Gender,
    Education = excluded.Education,
    OtherEducation = excluded.OtherEducation,
    Address = excluded.Address,
    ExtractionExperience = excluded.ExtractionExperience,
    ConsentChoice = excluded.ConsentChoice,
    InterventionType = excluded.InterventionType,
    SignaturePng = excluded.SignaturePng,
    PreQ1 = excluded.PreQ1,
    PreQ2 = excluded.PreQ2,
    PreQ3 = excluded.PreQ3,
    PreQ4 = excluded.PreQ4,
    PreQ5 = excluded.PreQ5,
    PreTotal = excluded.PreTotal,
    PreCategory = excluded.PreCategory,
    PreCompletedAt = excluded.PreCompletedAt,
    PostQ1 = excluded.PostQ1,
    PostQ2 = excluded.PostQ2,
    PostQ3 = excluded.PostQ3,
    PostQ4 = excluded.PostQ4,
    PostQ5 = excluded.PostQ5,
    PostTotal = excluded.PostTotal,
    PostCategory = excluded.PostCategory,
    PostCompletedAt = excluded.PostCompletedAt,
    CompletedAt = excluded.CompletedAt;
";

    private static string GetSelectCommandText() => @"
SELECT
    SessionId, CreatedAt, QuestionnaireDate, MedicalRecordNumber, FullName,
    BirthPlace, BirthDate, Gender, Education, OtherEducation, Address,
    ExtractionExperience, ConsentChoice, SignaturePng,
    PreQ1, PreQ2, PreQ3, PreQ4, PreQ5, PreTotal, PreCategory, PreCompletedAt,
    PostQ1, PostQ2, PostQ3, PostQ4, PostQ5, PostTotal, PostCategory, PostCompletedAt,
    InterventionType
FROM CompletedSessions
ORDER BY CompletedAt ASC, FullName COLLATE NOCASE ASC;
";
}
