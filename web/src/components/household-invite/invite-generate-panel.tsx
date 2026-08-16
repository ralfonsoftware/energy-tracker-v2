import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

interface HouseholdInviteResponse {
  token: string
  expiresAtUtc: string
}

export function InviteGeneratePanel() {
  const { t } = useTranslation()
  const [generating, setGenerating] = useState(false)
  const [link, setLink] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleGenerate = async () => {
    setGenerating(true)
    setError(null)

    try {
      const response = await fetch('/api/household-invites', {
        method: 'POST',
        credentials: 'include',
      })

      if (!response.ok) {
        throw new Error(`Unexpected /api/household-invites response: ${response.status}`)
      }

      const invite = (await response.json()) as HouseholdInviteResponse
      // Built client-side, never a hardcoded host/origin — this must work identically on
      // self-host and Azure (AD-13).
      setLink(`${window.location.origin}/join/${invite.token}`)
      setCopied(false)
    } catch {
      setError(t('householdInvite.errorGeneric'))
    } finally {
      setGenerating(false)
    }
  }

  const handleCopy = async () => {
    if (!link) {
      return
    }

    try {
      await navigator.clipboard.writeText(link)
      setCopied(true)
    } catch {
      setError(t('householdInvite.errorGeneric'))
    }
  }

  return (
    <GlassCard className="flex w-full max-w-sm flex-col gap-2">
      {!link && (
        <Button variant="glass-primary" onClick={handleGenerate} disabled={generating}>
          {generating ? t('householdInvite.generating') : t('householdInvite.generateButton')}
        </Button>
      )}

      {link && (
        <div className="flex flex-col gap-2">
          <Label htmlFor="household-invite-link">{t('householdInvite.linkLabel')}</Label>
          <div className="flex items-center gap-2 rounded-[12px] border border-[rgba(40,70,50,0.12)] bg-[rgba(255,255,255,0.5)] p-2.5 dark:border-[rgba(210,235,220,0.14)] dark:bg-[rgba(220,245,230,0.05)]">
            <Input
              id="household-invite-link"
              value={link}
              readOnly
              className="h-auto border-0 bg-transparent p-0 shadow-none focus-visible:ring-0"
            />
            <Button type="button" variant="ghost" size="sm" onClick={handleCopy}>
              {copied ? t('householdInvite.copied') : t('householdInvite.copyButton')}
            </Button>
          </div>
          <p className="text-muted-foreground text-sm">{t('householdInvite.expiresNote')}</p>
        </div>
      )}

      {error && <p className="text-destructive text-sm">{error}</p>}
    </GlassCard>
  )
}
