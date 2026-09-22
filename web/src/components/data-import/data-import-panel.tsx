import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { GlassCard } from '@/components/ui/glass-card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { GLASS_MODAL_CLASSNAME } from '@/lib/glass-classnames'
import {
  ApiError,
  confirmHouseholdImport,
  HouseholdImportValidationError,
  uploadHouseholdImportFile,
  type HouseholdImportSummary,
} from '@/lib/household-import-api'
import { useHouseholdImportJobPoll } from '@/components/data-import/use-household-import-job'

// Story 7.2: the import half of "Data export & import" (DataExportPanel ships only the export
// half, Story 7.1). No dedicated UX mockup exists for this screen (same "No UX mockup" precedent
// DataExportPanel's own comment documents) — built directly against GlassCard/Button/Dialog
// conventions, reusing settings-page.tsx's own Logoff flow as the confirmation-dialog pattern.
//
// Flow: pick file -> upload+validate (no DB write yet) -> on failure, render UX-DR14's explicit
// "Import data fails validation" state listing every reported failure -> on success, an explicit
// "replace all data" confirmation dialog (AC #4) -> confirm -> poll the enqueued restore job to
// completion -> success/failure state.
type ImportStep = 'idle' | 'validating' | 'validationFailed' | 'confirmDialog' | 'confirming' | 'restoring' | 'success' | 'error'

export function DataImportPanel() {
  const { t } = useTranslation()
  const [step, setStep] = useState<ImportStep>('idle')
  const [failures, setFailures] = useState<string[]>([])
  const [summary, setSummary] = useState<HouseholdImportSummary | null>(null)
  const [token, setToken] = useState<string | null>(null)
  const [jobId, setJobId] = useState<string | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  // Guards against a rapid double-invocation firing two concurrent uploads before React flushes
  // the disabled-button re-render — same discipline as DataExportPanel's own downloadingRef.
  const uploadingRef = useRef(false)
  // Aborts an in-flight upload/confirm fetch on unmount (e.g. the member navigates away from
  // Settings mid-upload) — same AbortController discipline use-smart-plug-import-job.ts already
  // established for this exact class of concern, which this component's imperative onChange-driven
  // flow hadn't picked up (code review, Story 7.2 Pass 2).
  const abortControllerRef = useRef<AbortController | null>(null)
  useEffect(() => () => abortControllerRef.current?.abort(), [])

  const restoreJob = useHouseholdImportJobPoll(step === 'restoring' ? jobId : null)
  useEffect(() => {
    if (step !== 'restoring') {
      return
    }

    if (restoreJob.state === 'completed') {
      setStep('success')
    } else if (restoreJob.state === 'failed') {
      setErrorMessage(restoreJob.errorMessage)
      setStep('error')
    }
  }, [step, restoreJob.state, restoreJob.errorMessage])

  const resetToIdle = () => {
    setStep('idle')
    setFailures([])
    setSummary(null)
    setToken(null)
    setJobId(null)
    setErrorMessage(null)
  }

  const handleFileChosen = async (file: File) => {
    if (uploadingRef.current) {
      return
    }

    uploadingRef.current = true
    setStep('validating')
    setFailures([])
    setErrorMessage(null)

    const controller = new AbortController()
    abortControllerRef.current = controller

    try {
      const result = await uploadHouseholdImportFile(file, controller.signal)
      setToken(result.token)
      setSummary(result.summary)
      setStep('confirmDialog')
    } catch (err) {
      if (controller.signal.aborted) {
        return
      }
      if (err instanceof HouseholdImportValidationError) {
        setFailures(err.failures)
        setStep('validationFailed')
      } else if (err instanceof ApiError && err.detail) {
        setErrorMessage(err.detail)
        setStep('error')
      } else {
        setErrorMessage(t('settings.dataImport.errorGeneric'))
        setStep('error')
      }
    } finally {
      uploadingRef.current = false
    }
  }

  const handleInputChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (file) {
      void handleFileChosen(file)
    }
  }

  const handleConfirm = async () => {
    if (!token) {
      return
    }

    setStep('confirming')
    const controller = new AbortController()
    abortControllerRef.current = controller

    try {
      const newJobId = await confirmHouseholdImport(token, controller.signal)
      setJobId(newJobId)
      setStep('restoring')
    } catch (err) {
      if (controller.signal.aborted) {
        return
      }
      setErrorMessage(err instanceof ApiError && err.detail ? err.detail : t('settings.dataImport.errorGeneric'))
      setStep('error')
    }
  }

  return (
    <GlassCard className="flex flex-col gap-4">
      <h2 className="text-lg font-semibold">{t('settings.dataImport.heading')}</h2>
      <p className="text-muted-foreground text-sm">{t('settings.dataImport.description')}</p>

      <input ref={fileInputRef} type="file" accept="application/json" className="hidden" onChange={handleInputChange} />

      {(step === 'idle' || step === 'error') && (
        <>
          <Button
            type="button"
            variant="outline"
            className="self-start"
            onClick={() => fileInputRef.current?.click()}
          >
            {t('settings.dataImport.trigger')}
          </Button>
          {step === 'error' && errorMessage && <p className="text-destructive text-sm">{errorMessage}</p>}
        </>
      )}

      {step === 'validating' && <p className="text-muted-foreground text-sm">{t('settings.dataImport.validating')}</p>}

      {step === 'validationFailed' && (
        <div className="flex flex-col gap-2">
          <h3 className="text-destructive font-semibold">{t('settings.dataImport.validationFailedTitle')}</h3>
          <p className="text-sm">{t('settings.dataImport.validationFailedIntro')}</p>
          <ul className="list-disc pl-5 text-sm">
            {failures.map((failure, index) => (
              <li key={`${index}-${failure}`}>{failure}</li>
            ))}
          </ul>
          <Button type="button" variant="outline" className="self-start" onClick={() => fileInputRef.current?.click()}>
            {t('settings.dataImport.tryAgain')}
          </Button>
        </div>
      )}

      {step === 'restoring' && <p className="text-muted-foreground text-sm">{t('settings.dataImport.restoring')}</p>}

      {step === 'success' && (
        <div className="flex flex-col gap-2">
          <p className="text-sm">{t('settings.dataImport.success')}</p>
          <Button type="button" variant="outline" className="self-start" onClick={resetToIdle}>
            {t('settings.dataImport.successAction')}
          </Button>
        </div>
      )}

      <Dialog open={step === 'confirmDialog' || step === 'confirming'} onOpenChange={(open) => !open && step === 'confirmDialog' && resetToIdle()}>
        <DialogContent className={GLASS_MODAL_CLASSNAME}>
          <DialogHeader>
            <DialogTitle>{t('settings.dataImport.confirmTitle')}</DialogTitle>
            <DialogDescription>{t('settings.dataImport.confirmDescription')}</DialogDescription>
          </DialogHeader>
          {summary && (
            <div className="flex flex-col gap-1 text-sm">
              <p className="font-semibold">{t('settings.dataImport.confirmSummaryHeading')}</p>
              <ul className="list-disc pl-5">
                <li>{t('settings.dataImport.confirmSummaryHouseholdMembers', { count: summary.householdMembers })}</li>
                {summary.hasMainMeter && <li>{t('settings.dataImport.confirmSummaryHasMainMeter')}</li>}
                <li>{t('settings.dataImport.confirmSummaryMeterReadings', { count: summary.meterReadings })}</li>
                <li>{t('settings.dataImport.confirmSummaryMeterRegressionPrompts', { count: summary.meterRegressionPrompts })}</li>
                <li>{t('settings.dataImport.confirmSummaryTariffs', { count: summary.tariffs })}</li>
                <li>{t('settings.dataImport.confirmSummaryEvents', { count: summary.events })}</li>
                <li>{t('settings.dataImport.confirmSummaryRooms', { count: summary.rooms })}</li>
                <li>{t('settings.dataImport.confirmSummaryPowerPoints', { count: summary.powerPoints })}</li>
                <li>{t('settings.dataImport.confirmSummaryDevices', { count: summary.devices })}</li>
                <li>{t('settings.dataImport.confirmSummarySmartPlugReadings', { count: summary.smartPlugReadings })}</li>
                <li>{t('settings.dataImport.confirmSummaryStatusSnapshots', { count: summary.statusSnapshots })}</li>
                <li>{t('settings.dataImport.confirmSummaryAuditCorrections', { count: summary.auditCorrections })}</li>
              </ul>
            </div>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={resetToIdle} disabled={step === 'confirming'}>
              {t('settings.dataImport.cancel')}
            </Button>
            <Button variant="glass-confirm" onClick={() => void handleConfirm()} disabled={step === 'confirming'}>
              {step === 'confirming' ? t('settings.dataImport.confirming') : t('settings.dataImport.confirmProceed')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </GlassCard>
  )
}
